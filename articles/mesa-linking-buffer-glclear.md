---
title: Mesa/OpenGL 源码分析：Linking、VBO 与 glClear
llm_assisted: true
---

这篇补记主要回答三个零散但常被忽略的问题：Mesa 7.0 的 linking 阶段如何统一寄存器索引，VBO API 在驱动侧的实现，以及 `glClear` 到底是 CPU `memset` 还是 GPU 指令。

继上一篇关于 context 与 shader 编译的记录，我继续沿着 Mesa 7.0 的源码把剩余的零散部分收个尾。因为下一篇准备单独写 DRI 流程，这里就按照「Linking → Buffer → glClear」的顺序做一个阶段性总结。
--- MORE ---

> 本文由LLM根据草稿内容大幅度修改的来，只经过简单的人工review。

## Linking

Linking 阶段的主要工作是寄存器的重映射：

- 针对 varying、uniform 等不同来源的寄存器文件，处理成对应的 input / output.
- 把两个stage的输入/输出寄存器的编排到同一个线性的寄存器文件上，处理好index。

## VBO

1. `glGenBuffers`：只是在 CPU 端创建一个 `gl_buffer_object`，并分配一个在进程范围内唯一的 `GLuint` ID。此时除了分配结构体，没有触碰任何 GPU 资源。
2. `glBindBuffer`：GL context 中针对每个 target 都有一个指向 `gl_buffer_object` 的指针。Bind 操作就是把目标字段换成 ID 对应实例的指针，相当于切换当前「活跃」缓冲区。
3. `glBufferData`：将用户数据按 `size` 拷贝一份放在 CPU 侧缓存里。`usage` 字段只是记录在结构体上，是否合理使用交给使用者；驱动此时还没把数据提交给 GPU。
4. `glVertexAttribPointer` / `glEnableVertexAttribArray`：更多是更新 context 状态，把「哪个 attribute 读哪个 buffer、stride/how many components」这些信息记录下来，方便之后的 draw 调用统一读取。

整体上，这一串 API 更多是在维护一份「context 内的 VBO 元数据」，而不是当场去动 GPU 资源。

## `glClear`

`glClear` 是我最想弄明白的函数之一，因为它的成本和行为直接影响到 frame pacing。可惜在 Mesa 7.0 里，这段路径和 X Window System 绑得太深，想在不深入 GLX/Xlib 的情况下完全搞清楚并不现实。

目前能确认的大致行为是：

- Mesa 会根据当下的 drawable、pixmap、以及是否能直接访问 framebuffer 来决定到底是谁来清屏。
- 在某些配置下，它会直接调用 X11 的 API，请求服务器端完成清屏；在另一些情况里则会在 CPU 端对后备缓冲做 `memset`。
- 是否会下发一个真正的 GPU clear 指令？这个问题还是留到 Mesa 最新版本再说吧。

由于这一块涉及 GLX、DRI 甚至硬件驱动的分层，我决定留到调研更新版本的时候再回头深入。至于 clear 是否会 block GPU、是否存在延迟清理的机制，目前还没找到确凿答案。

## 后记

这篇算是对 Mesa 7.0 时期「零散但关键」几个操作的补记。VBO 细节、软件渲染路径、以及那个用 switch 虚拟机执行 shader IL 的实现都很有意思，但不是我现在最想了解的内容。考虑到时间有限，下一篇打算先把 DRI 的大流程梳理清楚，再正式切换到 Mesa 的较新版本上继续阅读。毕竟老版本动态调试还是不太方便，阅读起来也受限。
