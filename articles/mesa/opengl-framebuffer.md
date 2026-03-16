---
title: Mesa/OpenGL 源码分析：framebuffer
---

之前看了 Mesa 的早期版本实现之后，我又开始看 Mesa 的最近版本的 OpenGL 实现了。不过，只能说有点选择错误，OpenGL 的实现实在是有太多历史包袱，驱动有非常多替应用层做的处理、回退逻辑。其中掺杂了大量固定管线（Fixed-function pipeline）时代的遗留代码，非 OpenGL Core profile 的部分也很影响阅读体验。所以我想先转去看 Vulkan 的实现，但是在此之前，想先总结一下 framebuffer 相关的一些阅读内容：一方面算是梳理了 OpenGL 底层的一些结构；另一方面，Framebuffer 的概念在 Vulkan 中也有对应的设计，这也为后续学习 Vulkan 打下基础。

在 Mesa 中，`gl_framebuffer` 主要包含多个 `gl_renderbuffer` 作为附件 (Attachment)，可以想象成 Framebuffer 有多个插槽，每个插槽可以绑定到具体的 Renderbuffer 或 Texture。

对于 default framebuffer，即关联到 Window System 的 framebuffer 有：

1. **Double Buffer 机制**: 用于 Window System 的 framebuffer 包含 front / back 缓冲（如果是立体视觉 stereo 模式，还会叠加 left / right 组合）。
1. **Depth / Stencil Buffers**: 深度与模板缓冲。

而 user defined framebuffer 有：

1. **Color Buffers**: 支持 Multiple Render Targets (MRT) 的 color0-7。

--- MORE ---

### 有哪些 framebuffer

- MakeCurrent 中绑定的，用于呈现的 WinSysDrawBuffer / WinSysReadBuffer (即 Window System 提供的默认 Framebuffer，ID 为 0)
- context 当前 `glBindFramebuffer` 绑定的 DrawBuffer / ReadBuffer
- 应用显式创建的 Framebuffer Object (FBO)

### 三层结构

Framebuffer 相当于一个有很多插槽的结构，前面和 shader 关联，为 shader 提供写入的位置，后面和具体的资源关联，控制实际写入的内存。

```text
[Shader 输出] 
   | layout(location = i)
   v
[Draw Buffer 映射] (API: glDrawBuffer / glDrawBuffers，对应内部 _ColorDrawBufferIndexes)
   | 
   v
[Framebuffer 插槽] (如 color0-7, depth, stencil)
   | glFramebufferTexture2D / glFramebufferRenderbuffer
   v
[物理内存资源] (Attachment：具体的 Texture 或 Renderbuffer)
```

**Note:**
1. **glDrawBuffer vs glDrawBuffers**
   `glDrawBuffer` 相当于只能处理 `layout(location = 0)`，但是 `glDrawBuffer` 可以做到 `location 0` 映射到 front + back，即一次性写到前后缓冲这种操作。
1. `ColorDrawBuffer[]` 是 API 的直接对应，而 `_ColorDrawBufferIndexes[]` 是内部 state tracker (st) 的。两者的区别是 `glDrawBuffer` 可以通过一个掩码 (`ColorDrawBuffer`) 对应于多个 buffer (`_ColorDrawBufferIndexes`)。
   Q: 为什么不只保留 indexes 呢？ 
   A: 因为 `glGet` 需要能拿到原始数据。虽然理论上可以用 user/window system framebuffer + indexes 反算出来，但这会增加复杂度。

## Window System Framebuffer

1. `st_framebuffer_reuse_or_create` 创建 window system 的 framebuffer, 并且创建了 0x0 大小的 color(back_left) / depth_stencil 的 renderbuffer, 此时还没有 texture。
   每个 context 会复用 framebuffer, 从而避免多次 MakeCurrent 时的重复创建。
1. 上一步的 renderbuffer 大小是 0x0，需要向底层 window system 查询最新的窗口大小，据此再创建 color / depth_stencil renderbuffer.
   - 在 `dri_drawable.textures/msaa_textures` 中创建新的 `pipe_resource`.
   - 设置 `pipe_surface` (`gl_renderbuffer.surface`) 来包裹新的 `pipe_resource`.
1. 和 `pipe_frontend_drawable` 的关联：
   `pipe_frontend_drawable` 的 `winsys_buffers` 字段是一个串联了 `gl_framebuffer` 的链表。
