---
title: Mesa/OpenGL 源码分析：Direct Rendering Infrastructure
llm_assisted: true
---

之前看的所有 Mesa 7.0 的代码都是以 "linux" config 为构建目标。而如果我理解正确的话，是一个基本上纯软件渲染的实现。根据 Gemini 的说法，Direct Rendering Infrastructure (DRI) 是 Mesa 正式引入广泛的硬件支持框架。

本文将简单的分析一下 Mesa 的 "linux-dri" 构建和 "linux" 的区别。

--- MORE ---

## glXCreateContext

区别在创建`GLXContext`的时间就开始了，这里会调用 `XF86DRIGetClientDriverName` 去获取 X Window System display 的厂商，然后去 dlopen 厂商对应的 DRI 动态库。

接着会去调用动态库暴露的 `__driCreateNewScreen_20050727` 去创建一个 "screen private" structure。这里似乎就是DRI初始化的地方，因为在调用前先调用了 XF86DRI 提供的诸如 `XF86DRIOpenConnection` 的API 创建了一个 DRM handle 传给 `driCreateNewScreen`.

这个函数创建中会做的事情包括：
- 创建 `__DRIscreenPrivate` 其中有些字段是通过 DRM query 内核获取到的，包括读取硬件寄存器（MMIO）。
- 填充 `__DRIscreenRec` 结构体中的函数指针，包括 `createNewContext`
- 创建 driver mode 或者说 `createContextModes`。注，这个的具体作用未详细了解。

`glXMakeCurrent` -> `bindContext`

总的来说，这里给出的信息说明了，DRI user mode driver (UMD) 会通过 DRI 的接口（背后应该是个系统调用）去做一些事情，这里主要是涉及到一些硬件寄存器的读取之类的操作。

## ISA

借助 r300 ISA 来大概看一看 GPU ISA 的一些差异，当然 r300 的和现代 GPU ISA 也会有一些差异，所以仅仅是作为参考。

1. ~~r300 中的指令集被切成一块一块，每一块最多包含 64 条 ALU 指令。这是很不同的一点，驱动需要自己去进行有点类似调度一样的管理。暂不清楚背后的硬件设计细节，但是感觉有点类似于 CPU ROB 的那种感觉，只是这里是指令级别的，而不是 μops 级别的限制。（令人有点好奇为什么有这个限制，为什么是一块中限制数量，况且 GPU 还不是乱序执行的）~~
   好吧被LLM坑咯。仔细读了下代码之后其实是这样的。r300的一个fragment shader确实做多只能有64条ALU，所以编译器甚至会去判断一条指令是否只用了 XYZ 分量或者 W 分量。因为同一条 ALU 指令可以对这两组分量执行不同的操作，从而把不同操作混到同一条指令中以复用指令。
   而这里所谓的分块其实是和 TEX 指令相关的，是为了解决 tex 的读写交错问题。分块内不允许对同一个 tex 进行读写两种操作。分块不影响 ALU 指令数量限制。
2. r300 指令中寄存器有四种类型：输入、输出、临时、常数。
   输出实际上是指令中的编码的几个bits。
   输入和临时都是通常的GPU寄存器。
   常数是指令中的一个特殊bit，类似于从另一套寄存器读取的样子。
3. 三角函数没有使用泰勒展开，而是先约到`[-π,π]`然后用一个经过 Abs 处理成奇函数的四次式进行估计。其在`[0, π]`上的整体误差看起来比泰勒展开前三项好。r300没有专门的指令。
   ```latex
   3.56*(x/π)^4 - 7.12*(x/π)^3 + 0.45 * (x/π)^2 + 3.11 * x/π
   ```
4. texture指令中的texture unit是encode在指令编码中的，这也就是为什么 GLSL 中不能动态的指定 texture unit 的原因。
5. Mesa IL 转码成 r300 ISA 的时候，常数寄存器的source是一个指针，指向硬编码的 static array 或者 Mesa 的 param array。这样每次执行的时候，可以通过更新数据到指针指向的空间，然后上传的时候从指针读取最新的数据给GPU。这种就是那些state parameter的更新方式。

## Input/Output

- vertex shader 的寄存器有两种：常规的寄存器和attribute寄存器
- pixel shader 的 output 是通过指令编码中的两个 bits 控制。问题是输出到的texture何时绑定？

## Render
像 `glDrawArrays` 这种实际上是写入了 CPU 端的一个 command buffer, 这个 command buffer 会在满的时候或者 flush 的时候使用 DRM 的一个API发给内核再到GPU。像是 shader 编译之后的 ISA 都是作为对GPU寄存器的修改，以一个command的方式发送给GPU。在Mesa的老版本中，包括编译的指令、VBO这些似乎都是每次render会写入一次，没有避免多次写入相同数据。VBO的数据是通过申请一个GART内存（一种CPU和GPU共享内存），如何CPU端拷贝至GART内存，然后把GART的指针写入寄存器发送给GPU。这里可变的state常数就会被固定下来。
