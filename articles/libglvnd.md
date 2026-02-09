---
title: libglvnd 实现分析
---

```
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

1. Application 用 `dlsym` 拿取 `glXGetProcAddress` 或者 `glXGetProcAddressARB` 的地址。
2. 传入函数名字符串，调用 `glXGetProcAddress` 获取 gl 函数的地址。

## stub 机制

每个 wrapper library + libGLdispatch 都会有一份 `stubPatchCallbacks` 负责修改自己的代码段中的 stub.
然后这些会把都会把自身的 callback 注册到 libGLdispatch (`__glDispatchRegisterStubCallbacks`, 只有一份代码)。从而 libGLdispatch 知道有几个地方需要 patch.
之后 vendor 可以选择去修改 entrypoint 的机器码从而提高效率。即直接改写 libGL.so & libGLdispatch.so 的 wtext section 中的机器码。
不过 libglvnd 不支持多线程不同 vendor 的 context，除非 vendor 检查到这种情况主动不 patch 或者用环境变量 disable.


## 优化：
1. libGLdispatch.so 通过使用 `dlsym(dlopen(NULL), "pthread_xxx")` 的方式来判断 exe 是否引用了 pthread 来动态的决定是否使用锁来保证多线程安全，从而优化单线程程序性能。

## libGL.so
- `libGL.so!glXxxx`:
  - GLX Core functions:
    `libGLX.so` export了一个函数指针数组 `__GLXGL_CORE_FUNCTIONS`, 直接转发到对应的函数指针。
    包括 GLX 本体的 <= 1.4 version 的函数，去掉 1.2 的 `glXGetCurrentDisplay` (不知道为什么，是bug?) 加上了 "GLX_ARB_get_proc_address" extension 定义的 `glXGetProcAddressARB`.

    ```asm
    call __GLXGL_CORE_FUNCTIONS@libGLX + offset
    ```
  - GLX Extension functions:
    化简过的伪代码：
    ```c
    if (libGL!cache_ptr != 0) call cache_ptr
    else {
      cache_ptr = libGLX!__glXGLLoadGLXFunction(name)
      if (cache_ptr) call cache_ptr
      else return default
    }
    ```
- `libGL.so!glxxx`:
  直接暴露的 GL function, 而不是通过 `glXGetProcAddress` 获取的地址。
  ```c
  libGL.so:
  section wtext: ; libglvnd custom section
  func_ptr *dispatch_table = *(func_ptr**)(tls_base + tls_offset(_glapi_tls_Current));
  jmp dispatch_table[slot] -> libGLX_vendor.so
  ```
  `dispatch_table` 中的值是 `&vendor->glDispatch->table`, 调用 vendor library 和 libGLX 之前的 API 中的 `getProcAddress` 函数拿到的地址。

  `glXGetProcAddress` 获取的地址的区别是，query得到的地址是 libGLdispatch.so 的 wtext section。而 libGL.so export 的是自己 libGL.so 的 wtext section。

## libGLX.so

| 文件 | 主要职责 | 关键特征 |
| --- | --- | --- |
| libglx.c | Public API 接入层 | • 导出所有 PUBLIC 的 GLX 函数符号<br>• 应用程序直接调用的接口<br>• 上下文生命周期管理<br>• 线程状态管理 |
| libglxmapping.c | Vendor 分发调度层 | • 管理 vendor 库的加载 (dlopen)<br>• 维护 XID → vendor 的映射表<br>• 与 libGLdispatch 交互<br>• 动态生成 dispatch stubs |
| libglxproto.c | X11 协议通信层 | • 直接使用 Xlib 发送/接收 X11 请求<br>• 封装底层的 GLX 协议通信<br>• 查询 X server 端信息 |

## vendor contract
- so file name: `libGLX_{vendorName}.so.0`
- export main func: `__glx_Main`
  用来把 libGLX 的 export function 传给 vendor, 被把 vendor 的 export function 传回给 libGLX


## export functions
- 一部分 glX function 是 vendor-neutral 的，直接由 libGLX 实现。
  - glXQueryExtension
  - glXQueryVersion
  - glXGetProcAddress
  - glXGetProcAddressARB
  - glXGetCurrentContext
  - glXGetCurrentDrawable
  - glXGetCurrentReadDrawable
  - glXGetCurrentDisplay
- 其它 glX function 是 vendor 相关的，libGLX 会分发 + 可能的合并逻辑（比如是Display相关，多个Screen可能是不同vendor）
  vendor查找逻辑主要包括下面几个为 key 的哈希表： 1. Screen 2. GLXFBConfig
  分发方式主要包括：
  - 一部分 glX function 是 vendor 实现的 “静态的” dispatch (load vendor lib 的时候 query 得到的)。两部分发，第一从 Screen 找到 vendor, 然后直接调用 vendor 对应的实现。
    - `LookupVendorEntrypoints`

## additional extension functions
- glX extension functions
  除了几个特定的 extension 函数 `glXGetProcAddressARB`, `glXImportContextEXT`, `glXFreeContextEXT`, `glXCreateContextAttribsARB` 因为涉及到 libGLX 的内容，由 libGLX 提供外。
  其他的 extension 函数，会去查看是否有任意 vendor 提供了该 extension，是的话会直接转发给该 vendor. 即使有多个 vendor 也只会使用第一个。
  如果某个 extension 当前没有任意已加载 vendor 提供，会生成一个临时的跳板，指向一个 no-op function。等到后续加载 vendor 的时候，会再去尝试匹配，如果成功，则跳转到 vendor 的实现。

  代码分析：
  - 由 libGLX 实现的 glX 代码可以直接 export, `LOCAL_GLX_DISPATCH_FUNCTIONS`.
  - 而 glX extension functions 不需要 libGLX 参与（除了几个特殊的），只需要记录一下其地址就行，这就是 `dispatchIndexList`
  - 但是有一个需求是在 vendor lib 没有加载之前就要提供这些 extension function 的地址，为了解决这个问题 `glx_entrypoint_start` 被引入提供一个临时跳板。

- gl extension functions
  和 glX extension 也是类似的， `dynamic_stub_names` 是用来维护已出现的 extension function 的名字。
  不同的是两点：
  - gl 的函数都是和一个 context 绑定的，没有 context 就是 noop 的实现。而 context 是和 vendor 对应的，也就是每个 vendor 一份 dispatch table `vendor->glDispatch`.
  - 因为上一点 dispatch 是和 vendor 定义的，就不存在前面 glX 中的 vendor lazy load 的问题，所以没有临时的跳板，遇到一个新的 extension (遇到的定义是 user query address) 就会加载这个函数，没有就是用默认的 noop 实现。
    这里有一个点是，因为返回给 user 的是 TLS 索引的跳板（在没有 vendor assembly patch 的情况下）。所以所有已经创建 context 的 vendor 都需要去获取这个地址，更新其 dispatch table, 而不仅仅是当前调用的线程就行。


```
__glXDisplayInfoHash (uthash node)
 ├─ info : __GLXdisplayInfo
 │   ├─ dpy : Display* (key)
 │   ├─ clientStrings : char*[3]         // cash str for `glXGetClientString`
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

```
__glXVendorNameHash (uthash node)
 ├─ vendor : __GLXvendorInfo
 │   ├─ vendorID (incremental ID 1+)              // managed by libGLdispatch
 │   ├─ name (char*) (key)
 │   ├─ dlhandle (dlopen)
 │   ├─ dynDispatch (winsys vendor table)   // GLX 动态表, managed in libGLX
 │   │     (哈希: dispatch index -> 本 vendor 的实现地址)
 │   ├─ glDispatch (__GLdispatchTable)      // OpenGL 全局表, allocated in libGLdispatch, Q: who manage?
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

```
__GLXcontextInfoRec
 ├─ context : __GLXcontextRec * (key) (vendor created, opaque structure)
 ├─ vendor : __GLXvendorInfo *
 ├─ currentCount : int                      // init 0, inc by `glXMakeCurrent`
 └─ deleted : Bool
```



TLS in libGLdispatch
case 1: GLX
```
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


libGLX 暴露给 vendor 的 functions:
- `__glx_Main` 传入的 import
- 通过调用 `glxvc->setDispatchIndex` 传入的 WinSys Dispatch Functions
