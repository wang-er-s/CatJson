using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;

namespace CatJson
{
    /// <summary>
    /// Json解析器
    /// </summary>
    public static class JsonParser
    {
        static JsonParser()
        {
#if FUCK_LUA
            if (TypeUtil.AppDomain == null)
            {
                throw new Exception("请先调用CatJson.ILRuntimeHelper.RegisterILRuntimeCLRRedirection(appDomain)进行CatJson重定向");
            }
#endif
        }

        /// <summary>
        /// Json词法分析器
        /// </summary>
        public static JsonLexer Lexer { get; } = new JsonLexer();

        /// <summary>
        /// 序列化时是否开启格式化
        /// </summary>
        public static bool IsFormat { get; set; } = true;

        /// <summary>
        /// 序列化时是否忽略默认值
        /// </summary>
        public static bool IgnoreDefaultValue { get; set; } = true;
        
        private static readonly NullFormatter nullFormatter = new NullFormatter();
        private static readonly ArrayFormatter arrayFormatter = new ArrayFormatter();
        private static readonly ReflectionFormatter reflectionFormatter = new ReflectionFormatter();
        private static readonly PolymorphicFormatter polymorphicFormatter = new PolymorphicFormatter();
        
        /// <summary>
        /// Json格式化器字典
        /// </summary>
        private static readonly Dictionary<Type, IJsonFormatter> formatterDict = new Dictionary<Type, IJsonFormatter>()
        {
            //基元类型
            {typeof(bool), new BooleanFormatter()},
            {typeof(int), new Int32Formatter()},
            {typeof(long), new Int64Formatter()},
            {typeof(float), new SingleFormatter()},
            {typeof(double), new DoubleFormatter()},
            {typeof(string), new StringFormatter()},
            
            //容器类型
            {typeof(List<>), new ListFormatter()},
            {typeof(Dictionary<,>), new DictionaryFormatter()},
            
            //特殊类型
            {typeof(JsonObject), new JsonObjectFormatter()},
            {typeof(JsonValue), new JsonValueFormatter()},
            
            //Unity特有类型
            {typeof(Hash128), new Hash128Formatter()},
            
            //其他
            {Type.GetType("System.RuntimeType,mscorlib"),new RuntimeTypeFormatter()}   //Type类型的变量其对象一般为RuntimeType类型，但是不能直接typeof(RuntimeType)，只能这样了
        };

        /// <summary>
        /// 添加自定义的Json格式化器
        /// </summary>
        public static void AddCustomJsonFormatter(Type type, IJsonFormatter formatter)
        {
            formatterDict[type] = formatter;
        }
        
        /// <summary>
        /// 设置用于获取字段/属性的BindingFlags
        /// </summary>
        public static void SetBindingFlags(BindingFlags bindingFlags)
        {
            reflectionFormatter.SetBindingFlags(bindingFlags);
        }
        
        /// <summary>
        /// 添加需要忽略的成员
        /// </summary>
        public static void AddIgnoreMember(Type type, string memberName)
        {
            reflectionFormatter.AddIgnoreMember(type,memberName);
        }
        

        /// <summary>
        /// 将指定类型的对象序列化为Json文本
        /// </summary>
        public static string ToJson<T>(T obj)
        {
            InternalToJson(obj, typeof(T));

            string json = TextUtil.CachedSB.ToString();
            TextUtil.CachedSB.Clear();

            return json;
        }

        /// <summary>
        /// 将指定类型的对象序列化为Json文本
        /// </summary>
        public static string ToJson(object obj, Type type)
        {
            InternalToJson(obj, type);

            string json = TextUtil.CachedSB.ToString();
            TextUtil.CachedSB.Clear();

            return json;
        }
        

        /// <summary>
        /// 将指定类型的对象序列化为Json文本
        /// </summary>
        internal static void ToJson<T>(T obj, int depth)
        {
            InternalToJson(obj, typeof(T),null, depth);
        }
        


        /// <summary>
        /// 将指定类型的对象序列化为Json文本
        /// </summary>
        internal static void InternalToJson(object obj, Type type, Type realType = null, int depth = 1,bool checkPolymorphic = true)
        {
            if (obj == null)
            {
                nullFormatter.ToJson(null,type,null, depth);
                return;
            }

            if (obj is IJsonParserCallbackReceiver receiver)
            {
                //触发序列化开始回调
                receiver.OnToJsonStart();
            }

            if (realType == null)
            {
                realType = TypeUtil.GetType(obj,type);
            }
            
            if (checkPolymorphic && !TypeUtil.TypeEquals(type,realType))
            {
                //开启了多态序列化检测
                //并且定义类型和真实类型不一致
                //就要进行多态序列化
                polymorphicFormatter.ToJson(obj,type,realType,depth);
                return;;
            }

            if (!realType.IsGenericType)
            {
                if (formatterDict.TryGetValue(realType, out IJsonFormatter formatter))
                {
                    formatter.ToJson(obj,type,realType, depth);
                    return;
                }
            }
            else
            {
                if (formatterDict.TryGetValue(realType.GetGenericTypeDefinition(), out IJsonFormatter formatter))
                {
                    //特殊处理泛型类型
                    formatter.ToJson(obj,type,realType,depth);
                    return;
                }
            }

            if (obj is Array array)
            {
                //特殊处理数组
                arrayFormatter.ToJson(array,type,realType, depth);
                return;
            }
            
            //使用处理自定义数据类的formatter
            reflectionFormatter.ToJson(obj,type,realType,depth);
        }
        
        /// <summary>
        /// 将Json文本反序列化为指定类型的对象
        /// </summary>
        public static T ParseJson<T>(string json)
        {
            Lexer.SetJsonText(json);
            return (T)InternalParseJson(typeof(T));
        }

        /// <summary>
        /// 将Json文本反序列化为指定类型的对象
        /// </summary>
        public static object ParseJson(string json, Type type)
        {
            Lexer.SetJsonText(json);
            return InternalParseJson(type);
        }
        
        /// <summary>
        /// 将Json文本反序列化为指定类型的对象
        /// </summary>
        internal static T ParseJson<T>()
        {
            return (T) InternalParseJson(typeof(T));
        }
        
        /// <summary>
        /// 将Json文本反序列化为指定类型的对象
        /// </summary>
        internal static object InternalParseJson(Type type,Type realType = null,bool checkPolymorphic = true)
        {
            if (Lexer.LookNextTokenType() == TokenType.Null)
            {
                return nullFormatter.ParseJson(type,null);
            }

            if (realType == null && !ParserHelper.TryParseRealType(type,out realType))
            {
                //未传入realType并且读取不到realType，就把type作为realType使用
                //这里不能直接赋值type，因为type有可能是一个包装了主工程类型的ILRuntimeWrapperType
                //直接赋值type会导致无法从formatterDict拿到正确的formatter从而进入到defaultFormatter的处理中
                //realType = type;  
                realType = TypeUtil.CheckType(type);
            }
            
            
            object result;
            
            if (checkPolymorphic && !TypeUtil.TypeEquals(type,realType))
            {
                //开启了多态检查并且type和realType不一致
                //进行多态处理
                result = polymorphicFormatter.ParseJson(type, realType);
            }
            else if (formatterDict.TryGetValue(realType, out IJsonFormatter formatter))
            {
                //使用通常的formatter处理
                result = formatter.ParseJson(type, realType);
            }
            else if (realType.IsGenericType &&  formatterDict.TryGetValue(realType.GetGenericTypeDefinition(), out formatter))
            {
                //使用泛型类型formatter处理
                result = formatter.ParseJson(type,realType);
            }
            else if (realType.IsArray)
            {
                //使用数组formatter处理
                result = arrayFormatter.ParseJson(type,realType);
            }
            else
            {
                //使用处理自定义类的formatter
                result = reflectionFormatter.ParseJson(type,realType);
            }

            if (result is IJsonParserCallbackReceiver receiver)
            {
                //触发序列化结束回调
                receiver.OnParseJsonEnd();
            }

            return result;
        }

       
    }

}
