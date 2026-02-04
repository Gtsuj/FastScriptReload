using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HookInfo.Runtime
{
    /// <summary>
    /// 字段持有者 - 用于存储新增字段的值
    /// 使用泛型避免装箱/拆箱开销
    /// </summary>
    /// <typeparam name="T">字段类型</typeparam>
    public class FieldHolder<T>
    {
        /// <summary>
        /// 实际存储字段值的公共字段
        /// 公开访问允许 IL 层面使用 ldfld/stfld 指令直接操作
        /// </summary>
        public T F;

        /// <summary>
        /// 构造函数 - 初始化为默认值
        /// </summary>
        public FieldHolder()
        {
            F = default(T);
        }

        /// <summary>
        /// 构造函数 - 使用指定值初始化
        /// </summary>
        public FieldHolder(T initialValue)
        {
            F = initialValue;
        }

        /// <summary>
        /// 获取字段 F 的引用
        /// 用于支持 ldflda 指令（取字段地址）
        /// 示例：ref int fieldRef = ref holder.GetRef();
        /// </summary>
        /// <returns>字段 F 的托管引用</returns>
        public ref T GetRef()
        {
            return ref F;
        }

        public override string ToString()
        {
            return $"FieldHolder<{typeof(T).Name}>[{F}]";
        }
    }

    /// <summary>
    /// 字段解析器 - 管理特定类型的所有新增字段
    /// 每个热重载类型对应一个 FieldResolver<T>
    /// </summary>
    /// <typeparam name="TOwner">字段所属的类型</typeparam>
    public static class FieldResolver<TOwner>
    {
        /// <summary>
        /// 实例字段存储：实例 → (字段名 → FieldHolder)
        /// 使用 ConditionalWeakTable 确保实例被 GC 时，字段数据也能被回收
        /// </summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, object>> _instanceFieldStorage = new ();

        /// <summary>
        /// 静态字段存储：字段名 → FieldHolder
        /// 静态字段不依赖实例，直接存储在类型级别
        /// </summary>
        private static readonly Dictionary<string, object> _staticFieldStorage = new ();

        /// <summary>
        /// 字段初始化器：字段名 → 初始化函数
        /// 用于在首次访问字段时提供初始值
        /// </summary>
        private static readonly Dictionary<string, Func<object>> _fieldInitializers = new ();
        
        /// <summary>
        /// 注册字段初始化器 - 使用固定初始值
        /// 当字段首次被访问时，会使用此值作为初始值
        /// </summary>
        /// <param name="fieldName">字段名称</param>
        /// <param name="initialValue">初始值</param>
        public static void RegisterFieldInitializer(string fieldName, object initialValue)
        {
            if (string.IsNullOrEmpty(fieldName))
            {
                throw new ArgumentException("字段名称不能为空", nameof(fieldName));
            }
            
            lock (_fieldInitializers)
            {
                // 将泛型 Func<TField> 转换为 Func<object>
                _fieldInitializers[fieldName] = () => initialValue;
            }
        }

        /// <summary>
        /// 移除字段初始化器
        /// </summary>
        /// <param name="fieldName">字段名称</param>
        /// <returns>是否成功移除</returns>
        public static bool UnregisterFieldInitializer(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
            {
                return false;
            }

            lock (_fieldInitializers)
            {
                return _fieldInitializers.Remove(fieldName);
            }
        }

        /// <summary>
        /// 清除所有字段初始化器
        /// </summary>
        public static void ClearFieldInitializers()
        {
            lock (_fieldInitializers)
            {
                _fieldInitializers.Clear();
            }
        }

        /// <summary>
        /// 获取字段持有者 - 核心方法
        /// 从全局存储中获取或创建 FieldHolder<TField />
        /// </summary>
        /// <typeparam name="TField">字段类型</typeparam>
        /// <param name="instance">对象实例（静态字段传 null）</param>
        /// <param name="fieldName">字段名称</param>
        /// <returns>字段持有者</returns>
        public static FieldHolder<TField> GetHolder<TField>(object instance, string fieldName)
        {
            // 静态字段：instance 为 null
            if (instance == null)
            {
                return GetStaticFieldHolder<TField>(fieldName);
            }

            // 实例字段：正常处理
            return GetInstanceFieldHolder<TField>(instance, fieldName);
        }

        /// <summary>
        /// 获取实例字段持有者
        /// </summary>
        private static FieldHolder<TField> GetInstanceFieldHolder<TField>(object instance, string fieldName)
        {
            // 获取实例的字段存储字典
            Dictionary<string, object> fieldsDict;
            
            lock (_instanceFieldStorage)
            {
                if (!_instanceFieldStorage.TryGetValue(instance, out fieldsDict))
                {
                    fieldsDict = new Dictionary<string, object>();
                    _instanceFieldStorage.Add(instance, fieldsDict);
                }
            }

            // 获取或创建 FieldHolder
            lock (fieldsDict)
            {
                if (!fieldsDict.TryGetValue(fieldName, out var holderObj))
                {
                    // 首次访问，创建 FieldHolder
                    var holder = CreateFieldHolder<TField>(fieldName);
                    fieldsDict[fieldName] = holder;
                    return holder;
                }

                // 类型检查
                if (holderObj is FieldHolder<TField> typedHolder)
                {
                    return typedHolder;
                }
                else
                {
                    // 创建新的 FieldHolder 并替换
                    var newHolder = new FieldHolder<TField>();
                    fieldsDict[fieldName] = newHolder;
                    return newHolder;
                }
            }
        }

        /// <summary>
        /// 获取静态字段持有者
        /// </summary>
        private static FieldHolder<TField> GetStaticFieldHolder<TField>(string fieldName)
        {
            lock (_staticFieldStorage)
            {
                if (!_staticFieldStorage.TryGetValue(fieldName, out var holderObj))
                {
                    // 首次访问，创建 FieldHolder
                    var holder = CreateFieldHolder<TField>(fieldName);
                    _staticFieldStorage[fieldName] = holder;
                    return holder;
                }

                // 类型检查
                if (holderObj is FieldHolder<TField> typedHolder)
                {
                    return typedHolder;
                }
                else
                {
                    // 创建新的 FieldHolder 并替换
                    var newHolder = new FieldHolder<TField>();
                    _staticFieldStorage[fieldName] = newHolder;
                    return newHolder;
                }
            }
        }

        /// <summary>
        /// 创建字段持有者并应用初始化器
        /// </summary>
        private static FieldHolder<TField> CreateFieldHolder<TField>(string fieldName)
        {
            var holder = new FieldHolder<TField>();

            // 如果有初始化器，使用初始化器提供的值
            if (_fieldInitializers.TryGetValue(fieldName, out var initializer))
            {
                var initialValue = initializer();
                if (initialValue != null)
                {
                    holder.F = (TField)initialValue;
                }
            }

            return holder;
        }

        /// <summary>
        /// 存储实例字段值
        /// </summary>
        /// <typeparam name="TField">字段类型</typeparam>
        /// <param name="instance">对象实例</param>
        /// <param name="value">字段值</param>
        /// <param name="fieldName">字段名称</param>
        public static void Store<TField>(object instance, TField value, string fieldName)
        {
            var holder = GetHolder<TField>(instance, fieldName);
            holder.F = value;
        }

        /// <summary>
        /// 存储静态字段值（方法重载）
        /// </summary>
        /// <typeparam name="TField">字段类型</typeparam>
        /// <param name="value">字段值</param>
        /// <param name="fieldName">字段名称</param>
        public static void Store<TField>(TField value, string fieldName)
        {
            var holder = GetHolder<TField>(null, fieldName);
            holder.F = value;
        }

        /// <summary>
        /// 检查实例是否有动态添加的字段
        /// </summary>
        /// <param name="instance">对象实例</param>
        /// <returns>是否有动态字段</returns>
        public static bool HasDynamicFields(object instance)
        {
            return _instanceFieldStorage.TryGetValue(instance, out _);
        }

        /// <summary>
        /// 获取实例的所有动态字段名称
        /// </summary>
        /// <param name="instance">对象实例</param>
        /// <returns>字段名称集合</returns>
        public static IEnumerable<string> GetDynamicFieldNames(object instance)
        {
            if (_instanceFieldStorage.TryGetValue(instance, out var fieldsDict))
            {
                lock (fieldsDict)
                {
                    return new List<string>(fieldsDict.Keys);
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 清除实例的所有动态字段（谨慎使用）
        /// </summary>
        /// <param name="instance">对象实例</param>
        public static void ClearDynamicFields(object instance)
        {
            if (_instanceFieldStorage.TryGetValue(instance, out var fieldsDict))
            {
                lock (fieldsDict)
                {
                    fieldsDict.Clear();
                }
            }
        }
    }
}

