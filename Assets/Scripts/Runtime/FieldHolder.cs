using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FastScriptReload.Runtime
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
        /// 全局存储：实例 → (字段名 → FieldHolder)
        /// 使用 ConditionalWeakTable 确保实例被 GC 时，字段数据也能被回收
        /// </summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, object>> 
            _instanceFieldStorage = new ConditionalWeakTable<object, Dictionary<string, object>>();

        /// <summary>
        /// 字段初始化器：字段名 → 初始化函数
        /// 用于在首次访问字段时提供初始值
        /// </summary>
        private static readonly Dictionary<string, Func<object>> 
            _fieldInitializers = new Dictionary<string, Func<object>>();

        /// <summary>
        /// 注册字段初始化器
        /// </summary>
        /// <param name="fieldName">字段名</param>
        /// <param name="initializer">初始化函数</param>
        public static void RegisterFieldInitializer(string fieldName, Func<object> initializer)
        {
            _fieldInitializers[fieldName] = initializer;
        }

        /// <summary>
        /// 获取字段持有者 - 核心方法
        /// 从全局存储中获取或创建 FieldHolder<TField>
        /// </summary>
        /// <typeparam name="TField">字段类型</typeparam>
        /// <param name="instance">对象实例</param>
        /// <param name="fieldName">字段名称</param>
        /// <returns>字段持有者</returns>
        public static FieldHolder<TField> GetHolder<TField>(object instance, string fieldName)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance), 
                    $"无法获取字段 '{fieldName}' 的持有者：实例为 null");
            }

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
                    var holder = new FieldHolder<TField>();

                    // 如果有初始化器，使用初始化器提供的值
                    if (_fieldInitializers.TryGetValue(fieldName, out var initializer))
                    {
                        try
                        {
                            var initialValue = initializer();
                            if (initialValue != null)
                            {
                                holder.F = (TField)initialValue;
                            }
                        }
                        catch (Exception ex)
                        {
                            ImmersiveVrToolsCommon.Runtime.Logging.LoggerScoped.LogWarning(
                                $"字段 '{fieldName}' 初始化失败: {ex.Message}，使用默认值");
                        }
                    }

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
                    // 类型不匹配，可能是字段类型被修改了
                    ImmersiveVrToolsCommon.Runtime.Logging.LoggerScoped.LogError(
                        $"字段 '{fieldName}' 类型不匹配: 期望 {typeof(TField).Name}，实际 {holderObj?.GetType().Name}");
                    
                    // 创建新的 FieldHolder 并替换
                    var newHolder = new FieldHolder<TField>();
                    fieldsDict[fieldName] = newHolder;
                    return newHolder;
                }
            }
        }

        /// <summary>
        /// 存储字段值（可选方法，实际可以直接通过 holder.f 赋值）
        /// 提供此方法是为了保持 API 完整性
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
        /// 获取字段值（可选方法，实际可以直接通过 holder.f 访问）
        /// 提供此方法是为了 API 完整性和调试
        /// </summary>
        /// <typeparam name="TField">字段类型</typeparam>
        /// <param name="instance">对象实例</param>
        /// <param name="fieldName">字段名称</param>
        /// <returns>字段值</returns>
        public static TField Get<TField>(object instance, string fieldName)
        {
            var holder = GetHolder<TField>(instance, fieldName);
            return holder.F;
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

        /// <summary>
        /// 获取统计信息（用于调试）
        /// </summary>
        public static string GetStatistics()
        {
            // ConditionalWeakTable 无法直接获取条目数，只能估算
            return $"FieldResolver<{typeof(TOwner).Name}>: " +
                   $"已注册初始化器数量 = {_fieldInitializers.Count}";
        }
    }
}

