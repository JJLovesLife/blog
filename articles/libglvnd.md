---
title: libglvnd 实现分析
llm_assisted: true
---

在以前的 Linux 环境下， OpenGL 库的加载存在一个问题：多个厂商的实现没法很好地共存。具体来说，应用会用 `libGL.so` 这个 SONAME 去加载 OpenGL 实现，但是动态加载器的搜索路径通常是固定的（当然可以通过修改环境变量之类的方式去改变哪个 `libGL.so` 被加载，但是这样的方式是不现实的），因此以前没法很好地让多个厂商的实现并存，无法让多个进程选择不同的实现，甚至动态地去决定用哪个厂商的实现。
为此，现代 Linux 下的 `libGL.so` 的实现通常是由 libglvnd (The GL Vendor-Neutral Dispatch library) 实现的。这个实现允许多个厂商的 OpenGL 实现同时存在于机器上，允许多个进程动态地选择具体的实现。
它通过实现一个中心化的 `libGL.so` + 不同厂商提供的 `libGLX_{vendor}.so`, 根据 context 转发到对应厂商提供的 GL/GLX 实现，从而解决上述提到的问题。

```text
┌───────────────────────────┐
│        Application        │
└─────┬────────────────────┬┘
      │                    │
      │                    │
 glXGetProcAddress         glxxx return from glXGetProcAddress
┌─────▾────────────┐     ┌─▾────────────────┐
│      libGL       ├─────▸  libGLdispatch   │
└─────┬─────────┬──┘     └─┬────────────────┘
      │         gl functions   ┌────────────────────┐
      │         └──────────┴───▸ libGLX_{vendor}.so │
      glXxxx(Display*, ...)    └─▴──────────────────┘
┌─────▾────────────┐             glX extension function
│      libGLX      ├─────────────┘ ┌────────┐
│                  ├───────────────▸ libX11 │
└──────────────────┘               └────────┘
```

--- MORE ---

## 整体架构

### 各库职责

- **libGL.so** / **libOpenGL.so** 等等：和应用的交互层，将 GLX 调用转发到 libGLX，将 GL 调用转发到 libGLdispatch。
- **libGLX.so**：负责 GLX 协议厂商无关部分的处理、vendor 的加载与分发调度。
- **libGLdispatch.so**：负责 GL 调用的分发，通常是基于 TLS 的方式。
- **libGLX_{vendor}.so**：vendor 的具体 OpenGL 实现。

### libGLX 模块划分

libglx.c - Public API 接入层
- 导出所有 PUBLIC 的 GLX 函数符号
- 应用程序直接调用的接口
- 上下文生命周期管理
- 线程状态管理

libglxmapping.c - Vendor 分发调度层
- 管理 vendor 库的加载 (dlopen)
- 维护 XID → vendor 的映射表
- 与 libGLdispatch 交互
- 动态生成 dispatch stubs

libglxproto.c - X11 协议通信层
- 直接使用 Xlib 发送/接收 X11 请求
- 封装底层的 GLX 协议通信
- 查询 X server 端信息

## Vendor 管理

### Vendor 加载

- so file name: `libGLX_{vendorName}.so.0`
- export main func: `__glx_Main`
  用来把 libGLX 的 export function 传给 vendor, 并把 vendor 的 export function 传回给 libGLX

### 核心数据结构

```text
__glXDisplayInfoHash (uthash node)
 ├─ info : __GLXdisplayInfo
 │   ├─ dpy : Display* (key)
 │   ├─ clientStrings : char*[3]         // cache str for `glXGetClientString`
 │   ├─ vendors : __GLXvendorInfo**      // 每个 screen 对应的一个支持该 screen 的 vendor
 │   ├─ vendorLock : glvnd_rwlock_t       // 保护 vendors
 │   ├─ xidVendorHash : lkdhash           // XID -> vendor 映射表
 │   │     ( XID 都是 GLXDrawable 包括 GLXWindow (`glXCreateWindow`) GLXPixmap (`glXCreateGLXPixmap` / `glXCreatePixmap`) GLXPbuffer (`glXCreatePbuffer`))
 │   ├─ glxSupported : Bool               // X Server 是否支持 GLX 扩展
 │   ├─ glxMajorOpcode : int              // GLX ext
 │   ├─ glxFirstError : int               // GLX ext
 │   └─ libglvndExtensionSupported : Bool // GLX 扩展是否支持 GLX_EXT_libglvnd
 ├─ inTeardown : Bool                     // Display 关闭中标记
 └─ extCodes : XExtCodes*                 // `XAddExtension` 返回，通过一个 dummy ext 来监听 `XCloseDisplay` 从而执行清理工作
```

```text
__glXVendorNameHash (uthash node)
 ├─ vendor : __GLXvendorInfo
 │   ├─ vendorID (incremental ID 1+)              // managed by libGLdispatch
 │   ├─ name (char*) (key)
 │   ├─ dlhandle (dlopen)
 │   ├─ dynDispatch (winsys vendor table)   // GLX 动态表, managed in libGLX
 │   │     (哈希: dispatch index -> 本 vendor 的实现地址)
 │   ├─ glDispatch (__GLdispatchTable)      // OpenGL 全局表, managed & allocated in libGLdispatch
 │   │   ├─ currentThreads
 │   │   ├─ stubsPopulated                  // # of func addr added into table
 │   │   ├─ getProcAddress                  // libGLX!VendorGetProcAddressCallback
 │   │   ├─ getProcAddressParam             // &vendor
 │   │   └─ table                           // func_addr[], the value stored in TLS
 │   ├─ glxvc -> &imports
 │   ├─ patchCallbacks (opt, points to &outer patchCallbacks if supported)
 │   └─ staticDispatch (GLX 静态入口指针集，从 vendor's getProcAddress 拿到的函数)
 ├─ imports : __GLXapiImports (vendor!__glx_Main 填充的回调函数表)
 └─ patchCallbacks : __GLdispatchPatchCallbacks (若支持 patch, subset of &imports)
```

```text
__GLXcontextInfoRec
 ├─ context : __GLXcontextRec * (key) (vendor created, opaque structure)
 ├─ vendor : __GLXvendorInfo *
 ├─ currentCount : int                      // init 0, inc by `glXMakeCurrent`
 └─ deleted : Bool
```

### TLS 与线程状态

TLS in libGLdispatch, 这里是 GLX 的情况，还有 EGL 的情况，此处不再列举。

```text
__GLXThreadStateRec
 ├─ glas : __GLdispatchThreadState
 │   ├─ tag : int                               // GLX / EGL
 │   ├─ threadDestroyedCallback
 │   └─ priv : __GLdispatchThreadStatePrivateRec
 │       ├─ threadState : __GLdispatchThreadState // &glas
 │       ├─ vendorID : int                      // &vendor->vendorID
 │       └─ dispatch : __GLdispatchTable        // &vendor->glDispatch
 ├─ currentVendor : __GLXvendorInfo *
 ├─ currentDisplay : Display *
 ├─ currentDraw : GLXDrawable
 ├─ currentRead : GLXDrawable
 └─ currentContext : __GLXcontextInfoRec *
```

## 函数分发机制

### GLX 函数分发

一部分 glX function 是 vendor-neutral 的，直接由 libGLX 实现：
- glXQueryExtension
- glXQueryVersion
- glXGetProcAddress
- glXGetProcAddressARB
- glXGetCurrentContext
- glXGetCurrentDrawable
- glXGetCurrentReadDrawable
- glXGetCurrentDisplay

其它 glX function 是 vendor 相关的，libGLX 会分发 + 可能的合并逻辑（比如是 Display 相关，多个 Screen 可能是不同 vendor）。
vendor 查找逻辑主要包括下面几个为 key 的哈希表：
- Screen
- GLXFBConfig
- GLXDrawable
- GLXContext

分发方式主要包括：
- 一部分 glX function 是 vendor 实现的 "静态的" dispatch (load vendor lib 的时候 query 得到的)。两步分发，第一从 Screen 找到 vendor, 然后直接调用 vendor 对应的实现，见 `LookupVendorEntrypoints`.
- 另外的 glX extension function, 除了几个特定的 extension 函数 `glXGetProcAddressARB`, `glXImportContextEXT`, `glXFreeContextEXT`, `glXCreateContextAttribsARB` 因为涉及到 libGLX 的内容，和前一部分一样由 libGLX 提供外，其他的 extension 函数，会去查看是否有任意 vendor 提供了该 extension，是的话会直接转发给该 vendor. 即使有多个 vendor 也只会使用第一个。如果某个 extension 当前没有任意已加载 vendor 提供，会生成一个临时的跳板，指向一个 no-op function。等到后续加载 vendor 的时候，会再去尝试匹配，如果成功，则跳转到 vendor 的实现。

代码分析：
- 由 libGLX 实现的 glX 代码可以直接 export, `LOCAL_GLX_DISPATCH_FUNCTIONS`.
- 而 glX extension functions 不需要 libGLX 参与（除了几个特殊的），只需要记录一下其地址就行，这就是 `dispatchIndexList`
- 但是有一个需求是在 vendor lib 没有加载之前就要提供这些 extension function 的地址，为了解决这个问题 `glx_entrypoint_start` 被引入提供一个临时跳板。

#### GLX 函数在 libGL 中的转发

`libGL.so` 也提供了不用 `glXGetProcAddress` 获取的大部分 GLX function 的 ELF export，其中有一部分是上面提到的静态函数，这部分 `libGLX.so` export 了一个函数指针数组 `__GLXGL_CORE_FUNCTIONS`, `libGL` 直接转发到对应的函数指针。
这些函数包括 GLX 本体的 <= 1.4 version 的函数，去掉 1.2 的 `glXGetCurrentDisplay`（疑似是一个 bug） ，加上了 "GLX_ARB_get_proc_address" extension 定义的 `glXGetProcAddressARB`.

```asm
call __GLXGL_CORE_FUNCTIONS@libGLX + offset
```

然后由于历史原因， `libGL` 也 export 了许多 GLX 的 extension 函数，这部分是通过类似于替用户 query 然后 cache 的方式转发：

```c
if (libGL!cache_ptr != 0) call cache_ptr
else {
  cache_ptr = libGLX!__glXGLLoadGLXFunction(name)
  if (cache_ptr) call cache_ptr
  else return default
}
```

### GL 函数分发

同样 `libGL.so` 提供了大部分 GL function 的 ELF export, 其代码类似于：

```c
libGL.so:
section wtext: ; libglvnd custom section
func_ptr *dispatch_table = *(func_ptr**)(tls_base + tls_offset(_glapi_tls_Current));
jmp dispatch_table[slot] -> libGLX_vendor.so
```

`dispatch_table` 中的值是 `&vendor->glDispatch->table`, 调用 vendor library 和 libGLX 之前的 API 中的 `getProcAddress` 函数拿到的地址。

但是 `glXGetProcAddress` 获取的地址和 export 的地址不一样，其区别是，query 得到的地址是 libGLdispatch.so 的 wtext section。而 libGL.so export 的是自己 libGL.so 的 wtext section，尽管两者的机器码是一样的。

#### GL Extension 函数

和 glX extension 也是类似的，`dynamic_stub_names` 是用来维护已出现的 extension function 的名字。
不同的是两点：
- gl 的函数都是和一个 context 绑定的，没有 context 就是 noop 的实现。而 context 是和 vendor 对应的，也就是每个 vendor 一份 dispatch table `vendor->glDispatch`.
- 因为上一点 dispatch 是和 vendor 定义的，就不存在前面 glX 中的 vendor lazy load 的问题，所以没有临时的跳板，遇到一个新的 extension (遇到的定义是 user query address) 就会加载这个函数，没有就是用默认的 noop 实现。
  这里有一个点是，因为返回给 user 的是 TLS 索引的跳板（在没有 vendor assembly patch 的情况下）。所以所有已经创建 context 的 vendor 都需要去获取这个地址，更新其 dispatch table, 而不仅仅是当前调用的线程就行。

和 GLX extension 函数不同，大部分 GL extension 函数是厂商特定，`libGL` 没有提供 ELF export, 只能通过 `glXGetProcAddress` 获得 `libGLdispatch` 中的跳板地址。

## 性能优化
### Stub Patching 机制

每个 wrapper library + libGLdispatch 都会有一份 `stubPatchCallbacks` 负责修改自己的代码段中的 stub.
然后这些都会把自身的 callback 注册到 libGLdispatch (`__glDispatchRegisterStubCallbacks`, 只有一份代码)。从而 libGLdispatch 知道有几个地方需要 patch.
之后 vendor 可以选择去修改 entrypoint 的机器码从而提高效率。即直接改写 libGL.so & libGLdispatch.so 的 wtext section 中的机器码。
不过 libglvnd 不支持多线程不同 vendor 的 context，除非 vendor 检查到这种情况主动不 patch 或者用环境变量 disable.

### 多线程锁优化

libGLdispatch.so 通过使用 `dlsym(dlopen(NULL), "pthread_xxx")` 的方式来判断 exe 是否引用了 pthread 来动态的决定是否使用锁来保证多线程安全，从而优化单线程程序性能。
