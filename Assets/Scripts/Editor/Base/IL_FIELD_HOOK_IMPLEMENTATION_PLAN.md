# IL层面字段Hook实现计划

## 概述

本计划用于实现方案1：在IL层面处理新增字段的Hook，将字段访问替换为 `FieldResolver<TOwner>.GetHolder<TField>(instance, "fieldName").F` 的形式。

## 实现阶段

### 阶段一：数据结构扩展

#### 任务1：扩展HookTypeInfo类
- **文件**：`Assets/Scripts/Editor/Base/HookTypeInfo.cs`
- **操作**：
  - 添加 `Dictionary<string, FieldInfo> AddedFields` 属性
  - 存储新增字段信息（字段名、字段类型、声明类型）

#### 任务2：在Diff阶段检测新增字段
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.Diff.cs`
- **操作**：
  - 在 `CompareTypesWithCecil` 方法中比较字段
  - 检测 `newTypeDef.Fields` 中不在 `existingTypeDef.Fields` 中的字段
  - 将新增字段信息添加到 `HookTypeInfo.AddedFields`
  - 使用反射获取字段类型信息

### 阶段二：核心IL替换逻辑

#### 任务3：修改ModifyMethod方法签名
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 修改 `ModifyMethod` 方法，添加 `HookTypeInfo hookTypeInfo` 参数
  - 将 `hookTypeInfo` 传递给 `CreateInstruction` 方法
  - 更新 `ModifyCompileAssembly` 中调用 `ModifyMethod` 的地方

#### 任务4：创建字段检测辅助方法
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **方法**：`IsNewField(FieldReference fieldRef, HookTypeInfo hookTypeInfo)`
- **功能**：
  - 检查字段是否为新增字段
  - 通过 `hookTypeInfo.AddedFields` 或 `HookTypeInfoCache` 查找
  - 比较字段的声明类型和字段名

#### 任务5：创建方法引用构建辅助方法
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **方法**：`GetFieldResolverGetHolderMethodReference(ModuleDefinition module, TypeReference ownerType, TypeReference fieldType)`
- **功能**：
  - 构建 `FieldResolver<TOwner>` 类型引用
  - 构建 `GetHolder<TField>` 泛型方法引用
  - 返回完整的方法引用

#### 任务6：创建FieldHolder.F字段引用
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **方法**：`GetFieldHolderFReference(ModuleDefinition module, TypeReference fieldType)`
- **功能**：
  - 构建 `FieldHolder<TField>` 类型引用
  - 返回 `FieldHolder<TField>.F` 字段引用

### 阶段三：指令序列替换

#### 任务7：实现ldfld指令替换
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **方法**：`ReplaceFieldLoadWithGetHolder(ILProcessor processor, Instruction ldfldInst, FieldReference fieldRef, HookTypeInfo hookTypeInfo, ModuleDefinition module)`
- **功能**：
  - 识别 `ldfld` 指令
  - 在 `ldfld` 之前插入：
    - `ldstr "FieldName"`（字段名字符串）
    - `call GetHolder<TField>`（调用GetHolder方法）
  - 将 `ldfld Field` 替换为 `ldfld FieldHolder<TField>.F`
  - 返回新插入的指令列表（用于后续处理）

#### 任务8：实现stfld指令替换
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **方法**：`ReplaceFieldStoreWithGetHolder(ILProcessor processor, Instruction stfldInst, FieldReference fieldRef, HookTypeInfo hookTypeInfo, ModuleDefinition module, List<Instruction> allInstructions)`
- **功能**：
  - 识别 `stfld` 指令
  - 查找 `stfld` 之前的实例引用位置
  - 在实例引用之后、`stfld` 之前插入：
    - `ldstr "FieldName"`
    - `call GetHolder<TField>`
    - `dup`（复制FieldHolder引用，用于后续stfld）
  - 将 `stfld Field` 替换为 `stfld FieldHolder<TField>.F`
  - 处理 `dup` 指令的位置调整（如果原代码中有）

### 阶段四：修改CreateInstruction方法

#### 任务9：集成字段替换逻辑到CreateInstruction
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 修改 `CreateInstruction` 方法签名，添加 `HookTypeInfo hookTypeInfo` 和 `ILProcessor processor` 参数
  - 在 `FieldReference` 处理分支中：
    - 调用 `IsNewField` 检查是否为新增字段
    - 如果是新增字段，调用替换方法
    - 如果不是，执行原有逻辑

#### 任务10：重构ModifyMethod中的指令创建逻辑
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 修改 `ModifyMethod` 方法中的指令创建循环
  - 改为两遍扫描：
    - 第一遍：识别需要替换的字段访问指令
    - 第二遍：执行替换并创建新指令序列
  - 或者：使用 `ILProcessor` 的 `InsertBefore`/`Replace` 方法动态修改

### 阶段五：栈状态和分支处理

#### 任务11：更新MaxStackSize计算
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 在 `ModifyMethod` 中，插入指令后重新计算 `MaxStackSize`
  - 考虑插入的 `ldstr`、`call`、`dup` 指令对栈的影响
  - 使用 `MethodBody.ComputeMaxStackSize()` 或手动计算

#### 任务12：处理分支目标更新
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 插入指令后，所有后续指令的偏移量改变
  - 更新所有分支指令（`br`、`brtrue`、`brfalse` 等）的操作数
  - 使用 `ILProcessor` 的自动更新机制，或手动更新所有跳转目标

#### 任务13：处理异常处理块
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 检查 `MethodBody.ExceptionHandlers`
  - 确保插入指令后，异常处理块的 `TryStart`、`TryEnd`、`HandlerStart`、`HandlerEnd` 仍然正确
  - 可能需要更新异常处理块的范围

### 阶段六：特殊情况处理

#### 任务14：处理静态字段
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 识别 `ldsfld`/`stsfld` 指令（静态字段访问）
  - 静态字段的处理方式可能不同（需要确认FieldResolver是否支持静态字段）
  - 如果不支持，记录警告或跳过

#### 任务15：处理字段地址（ldflda）
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 识别 `ldflda` 指令（取字段地址）
  - 对于新增字段，需要获取 `FieldHolder.F` 的地址
  - 可能需要特殊处理：`call GetHolder` → `ldflda F`

#### 任务16：添加程序集引用
- **文件**：`Assets/Scripts/Editor/Base/ReloadHelper.IL.cs`
- **操作**：
  - 在 `AddRequiredReferences` 方法中，确保添加 `FastScriptReload.Runtime` 程序集引用
  - 如果尚未添加，则添加引用

### 阶段七：测试和验证

#### 任务17：添加测试用例
- **文件**：创建新的测试文件
- **测试场景**：
  1. 简单字段读取：`this.NewField`
  2. 简单字段写入：`this.NewField = value`
  3. 复合赋值：`this.NewField += 10`
  4. 嵌套访问：`this._test1.Arg2`
  5. 字段自增：`this.NewField++`
  6. 字段地址：`ref this.NewField`
  7. 静态字段（如果支持）

## 实现顺序建议

1. **阶段一**（任务1-2）：数据结构扩展
2. **阶段二**（任务3-6）：核心辅助方法
3. **阶段三**（任务7-8）：指令替换逻辑（先实现简单情况）
4. **阶段四**（任务9-10）：集成到CreateInstruction
5. **阶段五**（任务11-13）：栈状态和分支处理
6. **阶段六**（任务14-16）：特殊情况处理
7. **阶段七**（任务17）：测试验证

## 关键技术点

### 1. 指令序列识别
- 需要识别 `ldfld`/`stfld` 之前的实例加载指令
- 需要处理 `dup` 指令的位置

### 2. 栈状态管理
- 插入指令会改变栈状态
- 需要确保所有执行路径的栈状态一致

### 3. 分支目标更新
- 插入指令后，所有跳转目标需要更新
- 可以使用 `ILProcessor` 的自动更新机制

### 4. 异常处理块
- 插入指令后，异常处理范围可能受影响
- 需要验证和更新异常处理块

## IL替换示例

### 原IL代码（读取字段）
```
IL_001A: ldarg.0                    // 加载 this
IL_001B: ldfld     _test1           // 加载字段 _test1
IL_0020: dup                        // 复制引用
IL_0021: ldfld     Arg2             // 读取 Arg2 字段值
```

### 替换后IL代码
```
IL_001A: ldarg.0                    // 加载 this
IL_001B: ldfld     _test1           // 加载字段 _test1
IL_0054: ldstr     "Arg2"           // 加载字段名字符串
IL_0059: call      FieldResolver<Test1>.GetHolder<int>  // 调用 GetHolder
IL_005F: ldfld     F                // 读取 FieldHolder.F 字段值
```

### 原IL代码（写入字段）
```
IL_001A: ldarg.0                    // 加载 this
IL_001B: ldfld     _test1           // 加载字段 _test1
IL_0020: dup                        // 复制引用（用于后续 stfld）
IL_0021: ldfld     Arg2             // 读取当前值
IL_0026: ldc.i4.s  101              // 加载常量
IL_0028: add                        // 相加
IL_0029: stfld     Arg2             // 写入字段
```

### 替换后IL代码
```
IL_001A: ldarg.0                    // 加载 this
IL_001B: ldfld     _test1           // 加载字段 _test1
IL_0054: ldstr     "Arg2"           // 加载字段名字符串
IL_0059: call      FieldResolver<Test1>.GetHolder<int>  // 调用 GetHolder
IL_005E: dup                        // 复制 FieldHolder 引用（用于后续 stfld）
IL_005F: ldfld     F                // 读取 FieldHolder.F 当前值
IL_0064: ldc.i4.s  101              // 加载常量
IL_0066: add                        // 相加
IL_0067: stfld     F                // 写入 FieldHolder.F
```

## 注意事项

1. **字段类型推断**：从 `FieldReference.FieldType` 获取字段类型
2. **所有者类型确定**：从 `FieldReference.DeclaringType` 获取所有者类型
3. **实例引用识别**：需要识别 `ldfld`/`stfld` 之前的实例加载指令
4. **dup指令处理**：原代码中的 `dup` 位置需要调整
5. **MaxStackSize更新**：插入指令后需要重新计算栈深度

