---
title: 为什么一个老 P/Invoke bool bug 在 .NET 8 才暴露：一次关于 SkipLocalsInit 优化的追查
llm_assisted: true
---

记录一个快两年前修过的 bug。

当时 .NET 6 快要 end of life 了，我们把一个服务从 .NET 6 升级到了 .NET 8。升级之后，有用户报了一个看起来不太相关的数据异常 bug。一番略过不表的定位之后，发现问题的根源是其中一个 .NET 和 C++ 的交互面错误地使用了不同的 bool 数据长度，.NET 使用了默认的 4 字节，但是 C++ 期待的是 1 字节，因此高 3 个字节里的未初始化脏数据导致了数据异常。

不过我真正想分享的是后面多问的这一句：为什么之前没有用户报告这个问题，而且结果还挺稳定，但是升级到 .NET 8 之后，很快就有用户报了问题？到底为什么 .NET 6 下这个 bool 长度不匹配没有导致问题，但是 .NET 8 就会出问题？

--- MORE ---

.NET 6 中一个这样的函数：

```csharp
[DllImport(NativeLibraryName, EntryPoint = "set_false", CallingConvention = CallingConvention.Cdecl)]
internal static extern void set_false_default(out bool value);
```

生成的 ILStub 类似于：

```
ManagedInteropMethodName = set_false_default
ManagedInteropMethodSignature = void(bool&)
NativeMethodSignature = unmanaged cdecl void(native int)
StubMethodSignature = void(bool&)

.maxstack 3
.locals (int32,bool,int32)

// Marshal {
         ldc.i4.0
         stloc.0
IL_0002: nop
         ldc.i4.0
         conv.i4
         stloc.2
// } Marshal

// CallMethod {
         ldloca.s        0x2
         conv.i
         ...
         calli           unmanaged cdecl void(native int)
// } CallMethod

// Unmarshal {
         ...
// } Unmarshal
```

这里关键的地方是传给 C++ 的 `bool*` 是第三个 local 的地址，而 .NET 6 生成的 stub 会先执行 `ldc.i4.0; conv.i4; stloc.2`，也就是把这 4 个字节的临时变量初始化成 0。随后 C++ 的 `bool*` 虽然只写了最低的 1 个字节，剩下 3 个字节仍然保持为 0，所以回到 managed 之后再按 4 字节 `BOOL` 判断，也会稳定得到 `false`。

到了 .NET 8，一方面我们改成了 LibraryImport 以使用 source generators:

```csharp
[LibraryImport(NativeLibraryName, EntryPoint = "set_false")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
internal static partial void set_false_bool([MarshalAs(UnmanagedType.Bool)] out bool value);
```

不过这里生成的 source generated code 大概是这样：

```csharp
[global::System.Runtime.CompilerServices.SkipLocalsInitAttribute]
internal static partial void set_false_bool(out bool value)
{
	global::System.Runtime.CompilerServices.Unsafe.SkipInit(out value);
	int __value_native;
	{
		__PInvoke(&__value_native);
	}

	// Unmarshal - Convert native data to managed data.
	value = __value_native != 0;
}
```

关键是这里的 `SkipLocalsInit`。.NET 8 中引入了 P/Invoke 的 zeroing 优化，这使得用来接收 C++ 返回值的局部变量跳过了初始化，处在一个未初始化状态。等 C++ 修改了最低的 1 个字节返回之后，高 3 个字节还是未初始化的脏数据，于是后面的 `__value_native != 0` 这个 bool 规整化就有可能产出错误的数据。

整个刨根究底的过程虽然没有完整记录下来，但是最后从一个定位数据异常到发现 P/Invoke bool 大小不匹配的 bug，一路追到 .NET 8 的 `SkipLocalsInit` 优化，还是我印象中一个比较有趣的故事。
