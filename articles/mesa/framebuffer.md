`gl_framebuffer`

1. 有多个 `gl_renderbuffer`, 包括：
   Multiple Render Targets 的 color0-7
   depth / stencil
   double buffer (front/back) × (left / right (if stereo))
1. 和 `pipe_frontend_drawable` 的关联：
   `pipe_frontend_drawable` 的 `winsys_buffers` 字段是一个串联了 `gl_framebuffer`.
1. `Attachment` 见 [glFramebufferTexture2D](#三层结构), framebuffer 插槽绑定的 renderbuffer / texture.


### 有哪些 framebuffer

- MakeCurrent 中绑定的，用于呈现的 WinSysDrawBuffer / WinSysReadBuffer
- context 当前 `glBindFramebuffer` 绑定的 DrawBuffer / ReadBuffer
- 显式 named framebuffer

### 三层结构

framebuffer 相对于一个有很多插槽的一个结构，前面和 shader 关联，为 shader 提供写入的位置，后面和具体的资源关联，控制实际写入的 mem.

in shader: `layout(location = i)`
|- glDrawBuffer / glDrawBuffers; `gl_framebuffer._ColorDrawBufferIndexes`
插槽 in framebuffer: color0-7, depth, stencil, depth_stencil
|- glFramebufferTexture2D / glFramebufferRenderbuffer; `gl_framebuffer.Attachment`
插入到 framebuffer 插槽的资源，是 texture oir renderbuffer

Note:
1. glDrawBuffer vs glDrawBuffers
   glDrawBuffer 相当于只能处理 `layout(location = 0)`, 但是 glDrawBuffer 可以做到 location 0 -> front + back, 即一次性写到前后缓冲这种。
1. `ColorDrawBuffer[]` 是 API 的直接对应，而 `_ColorDrawBufferIndexes[]` 是内部 stack tracker 的。两者的区别是 glDrawBuffer 可以一个 mask (`ColorDrawBuffer`) 对应于多个 buffer (`_ColorDrawBufferIndexes`)。
   Q: 为什么不只保留 indexes 呢？ A: `glGet` 要能拿到原始数据。虽然理论上可以用 user/windows framebuffer + indexes 反算出来，但是增加了复杂度。



## Windows System Framebuffer

1. `st_framebuffer_reuse_or_create` 创建 windows system 的 framebuffer, 并且创建了 0x0 大小的 color(back_left) / depth_stencil 的 renderbuffer (no texture yet)
   每个 context 会 reuse framebuffer 来避免多次 make current 重复创建。
1. 上一步的 renderbuffer 大小是 0x0, 需要 query windows system 拿到最新的窗口大小，据此创建 color / depth_stencil renderbuffer.
   - 创建新的 `pipe_resource` in `dri_drawable.textures/msaa_textures`.
   - 设置 `pipe_surface` (`gl_renderbuffer.surface`) 包裹新的 `pipe_resource`.





---

glx_screen 派生类负责实现的功能：
- 查询窗口系统中的窗口大小。根据spec这个是自动更新的，不需要用户介入。 e.g., `XGetGeometry`
// TODO: [DRI3] drisw_allocate_textures.imported_buffers 可以不在 gallium 中创建 renderbuffer 而直接引入？


pipe_screen 派生类负责实现的功能：
- [创建 pipe_resource](#pipe_resource)


### pipe_resource
有两种： display target （即用于呈现画面的，需要和窗口系统交互去 present的）; 其它的正常 texture 资源。

create:
- softpipe
  如果是 display target, 会创建 `dri_sw_displaytarget`, 其会创建对应 image 大小的一个共享内存（方便和X server传递）或者是 malloc 一块 image 大小的内存（fallback）。
  其它 resource 会 malloc 创建资源，包括 mipmap + texture array.
