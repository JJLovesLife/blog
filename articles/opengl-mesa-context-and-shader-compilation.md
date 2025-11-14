---
title: "Mesa/OpenGL 源码分析：Context 与 Shader 编译机制"
llm_assisted: true
---

TL;DR;
在 Mesa 早期版本的实现中，OpenGL context 其实就是一个存在 TLS 中的大结构体，保存了诸如固定管线状态、shader 源代码、已编译的 IL 之类的东西。
而 `glShaderSource` 只是复制了源代码，`glCompileShader` 则是对源代码进行预处理、语法语义分析，并生成 Mesa 定义的 IL 代码。

前段时间 AMD 有一个关于 AMDVLK 开源项目 discontinue 的 [announcement](https://github.com/GPUOpen-Drivers/AMDVLK/discussions/416)，其中提到他们准备转向 RADV，这是一个 Mesa 下属的 Vulkan driver。于是借此机会，我也想花点时间来看看这个 Mesa 项目。

--- MORE ---

考虑到如今的 Mesa 已经非常庞大，我本次的切入点是 Mesa 6.4 - 7.0 这段时间的版本。这一段时间也正好是 Mesa 为 OpenGL 2.0 增加支持，即引入可编程管线的时期。这使其框架更接近现代化设计，而 shader 这部分正好也是我最感兴趣的部分。

这篇文章是 Mesa 源码分析的第一篇文章，主要探讨我比较关心的两个核心概念：OpenGL 的上下文（Context）在 Mesa 中的实现，以及 Shader 的编译流程。

## OpenGL 的 context

我们知道，像 Direct3D 12 / Vulkan 这些新 API 在性能上优于 Direct3D 11 / OpenGL 的一个关键点就是对多线程的支持。尽管我目前尚未完全清楚这几个 API 在多线程支持上的区别到底在哪里，但 OpenGL 中有一个很可能与此相关的概念就是所谓的 context。

OpenGL 本身没有直接提供 API 来管理 Context，但执行任何指令都需要一个预先存在的 context。在 OpenGL 2.0 specification 中提到这么一句话：

> Issuing GL commands when the program is not connected to a context results in undefined behavior.

而这个 context 的创建更多是和操作系统，甚至是所使用的 GUI 系统关联的。比如在 Unix-like 系统下依赖于 X Window System 的 GLX API，它提供了一个 `glXCreateContext` 来创建 Context。在 Mesa 提供的 GLX API 实现中，`glXCreateContext` 的一个子流程就是创建 Mesa 的 OpenGL Context。

这个 context 本身就是一个大的结构体，其定义在[这里](https://gitlab.freedesktop.org/mesa/mesa/-/blob/mesa-6.4.1/src/mesa/main/mtypes.h#L2660-2882)，而其中的一大部分字段是用于 OpenGL 1.0 中的固定管线的数据。

这个 context 在被激活时，比如调用 `glXMakeCurrent` 的时候，会把这个结构体的指针存到一个用来标记当前 active context 的变量上。进一步的，在 Mesa 6.4 中有一个 `GLX_USE_TLS` 的宏，当定义时，这个变量会是一个 TLS 变量。也就是说，其它线程调用 OpenGL API 的时候会拿不到 context 从而调用失败。

```cpp
// https://gitlab.freedesktop.org/mesa/mesa/-/blob/mesa-6.4.1/src/mesa/glapi/glapi.h#L59
const extern void *_glapi_Context;
const extern struct _glapi_table *_glapi_Dispatch;

extern __thread void * _glapi_tls_Context
    __attribute__((tls_model("initial-exec")));
```

这里还可以看到一个 `_glapi_Dispatch`，这其实就是一个 function table，它是实际上的 OpenGL API 调用后立刻跳转的函数。在 `glXMakeCurrent` 中也会将 `context->CurrentDispatch` 设置为当前的 active dispatch table。

然后 context 中还有一个可以被多个 context share 的部分，即可多线程共享的部分，其中就保存了所有 shader 的源代码和编译过的 IL:

```cpp
// https://gitlab.freedesktop.org/mesa/mesa/-/blob/mesa-6.4.1/src/mesa/main/mtypes.h#L2683-2684
   /** State possibly shared with other contexts in the address space */
   struct gl_shared_state *Shared;
```

## Shader 编译机制

另一个我比较想了解的就是 shader 的一系列 API 了。所以我看了下 Mesa 7.0 的实现，这是 Mesa 刚开始支持 OpenGL 2.0 的版本（当然之前的版本已经有一些通过厂商扩展的方式进行尝试了）。

到目前为止，我主要看了下 Mesa 对于下面三个 API 的实现：

- `glCreateShader`
- `glShaderSource`
- `glCompileShader`

`glCreateShader` 和 `glShaderSource` 其实是两个非常简单的函数：第一个在上文提到的 `Shared` 状态中创建一个`gl_program`结构体；第二个将 shader 的源代码字符串拷贝一份私有的到创建的结构体中。

真正开始有意思的是 `glCompileShader`。在这个函数中，Mesa 用了一套自己的语法框架（据我所知在新的 Mesa 版本中已经弃用）对 shader 源代码进行了宏的预处理、语法检查、语义检查，并最终构建出了一棵语法树。关键在于，它会进一步把语法树处理并生成一个中间语言（我不是很确定这个是不是 Mesa 自有的，还是一套公认的 IL，而且我尚未了解 IL 到最终 binary 的部分）。这部分的代码库被称之为 `slang`。并且 `slang` 在 emit IL 的时候进行了一些简单的优化，比如一些常数折叠和 MAD 优化。此外还有寄存器的分配、输入输出寄存器的规划等。

生成的结果是一个线性的指令块，包括使用到的一些子函数（尽管 `slang` 会做一些 inline，但我不确定是否把整个 shader 完全 inline 了）。生成之后的 IL 会保存在通过 `glCreateShader` 创建的结构体中。哦对了，Mesa 7.0 对于很多 GLSL builtin 的东西也使用了 GLSL 来描述，并通过引入一个 `__asm` 指令来实现这些内置函数。但是它的 `glCompileShader` 每次都会重复处理这些 builtin GLSL，效率很低。

## 待续

下面我准备进一步看看后面 shader 的逻辑。一个是我似乎看到 Mesa 会做一些 link，是不是说 vertex shader 和 pixel shader 会 link？另外就是 IL 生成真正的 binary 是否是 Mesa 负责的？以及它和内核的交互接口到底是什么？等等，静待后文分析。
