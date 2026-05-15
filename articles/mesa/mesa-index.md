options
egl = disabled

Main output:
executable: vtn_bindgen2, mesa_clc
shared_library:
- src/gallium/targets/dri/libgallium-25.3.1.so
- src/gallium/targets/dril/libdril_dri.so
- src/gallium/targets/dril/{vendor}_dri.so
- src/egl/libEGL_mesa.so.0.0.0 or libEGL (no glvnd)
- src/gbm/libgbm.so.1.0.0
- src/gbm/backends/dri/dri_gbm.so
- src/glx/libGLX_mesa.so.0.0.0 or libGL (no glvnd)

Optional:
shared library for option "vulkan-layers"
executable for option "tools"
glcpp executable
perfetto's executable & shared_library

optional shared library:
- GLESv1_CM (not glvnd)
- GLESv2 (not glvnd)
- swrast: src/gallium/targets/lavapipe/libvulkan_lvp.so
- windows opengl:
  - opengl32
  - libgallium_wgl
- glx == dri: libGL.so
- options:
  - gallium-d3d10umd
  - gallium-mediafoundation
  - gallium-rusticl
  - teflon
- va dependency:
  - vaon12_drv_video & gallium_drv_video
- vulkan layers: VkLayer_MESA_{layer_name}

Done:

## 配置
最核心的配置是 driconf, 通过 drirc XML 配置文件配置。同时也可以通过对应名称的环境变量来覆盖。
```
$DRIRC_CONFIGDIR / $sysconfdir/drirc + $datadir/drirc.d/*.conf
+ $HOME/.drirc
```
剩下的有些零散的是读取环境变量等。


## Screen / Context / Drawable 层次架构

```
libGLX
─── glx_display
     │  ╔═ drisw_screen
     │  ╠═ dri3_screen
     └─ glx_screen[]
         │ ┆
         └─┆─ dri_screen
           ┆   │  ╔═ pipe_loader_sw_device
           ┆   │  ║   │   ╔═ dri_sw_winsys
           ┆   │  ║   └─ sw_winsys
           ┆   │  ╠═ pipe_loader_drm_device
           ┆   ├─ pipe_loader_device
libgallium ┆   └─ pipe_frontend_screen
           ┆       ├─ st_screen
           ┆       │  ╔═ softpipe_screen
           ┆       │  ╠═ dd_screen (debug)
           ┆       │  ╠═ trace_screen (debug)
           ┆       │  ╠═ noop_pipe_screen (debug)
           ┆       └─ pipe_screen
─── glx_context
     ├─ glx_context_vtable
     │ ╭┄┄┄╯
     └─┆─ dri_context
       ┆   └─ st_context
       ┆       ├─ gl_context
       ┆       │  ╔═ softpipe_context
       ┆       └─ pipe_context
       ╰┄┄┄╮
─▴─ XAllocID
 ┊ 1:1     ┆
 ┊ mapping ╎
 ┊    ╔═ drisw_drawable
 ╰┈┈▸__GLXDRIdrawable
         └─┆─ dri_drawable
           ┆   ├─ pipe_frontend_drawable
           ┆   │   └─ ID ◂┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈╮
           ┆   │  ╔═ softpipe_resource      ┊┈ each st_context has a map
           ┆   │  ║   ├─ sw_displaytarget   ┊
           ┆   │  ║   │   └─ data : byte[] // 共享内存 with X server
           ┆   │  ║   └─ data : byte[] // malloc
           ┆   └─ textures : pipe_resource ◂┊┈╮
           ┆                                ┊ ┊
           ┆  gl_framebuffer                ┊ ┊
           ┆   ├─ drawable_ID ◂┈┈┈┈┈┈┈┈┈┈┈┈┈╯ ┊
           ┆   └─ Attachment : gl_renderbuffer[] // color buffer, depth/stencil, MRT color0-7
           ┆       └─ Renderbuffer.texture ◂┈┈╯
```

Note 1: 关于 `pipe_frontend_drawable` 和 `gl_framebuffer` 的这个 ID 对应。 `__GLXDRIdrawable` 是可以被多个 context 共享的！（尽管这种共享是有风险的，渲染到同一个窗口需要一些 Mesa 之外的同步机制）因此 `gl_framebuffer` 不能作为 `pipe_frontend_drawable` 的一个字段（当然也不符合 Mesa 的分层思想）。但是为了避免多次 MakeCurrent 重复创建，通过 `st_context.winsys_buffers` 来缓存每个 context 中的实例。


## Screen

dri3_create_screen -> `dri3_screen`
  可能包含一个 `dri_screen` （比如集显显示，独显渲染）

> libGLX interface
```
glx_screen
 ├─ vtable : const glx_screen_vtable*
 ├─ context_vtable : const glx_context_vtable*
 ├─ driverName : char* // 哪个 dri_driver
 ├─ frontend_screen : dri_screen* // libgallium
 ├─ driScreen : __GLXDRIscreen
 │   ├─ 函数指针
 │   └─ maxSwapInterval : int
 │
 │ # display 关联
 ├─ display : glx_display*
 ├─ dpy : Display*
 ├─ scr : int
 │
 │ # visual / fbconfig 链表，是 Xlib 返回和 libgallium 返回的 driver_configs 的交集
 ├─ visuals : glx_config*
 ├─ configs : glx_config*
 ├─ driver_configs : const dri_config** // libgallium 创建的 visual / fbconfig
 │
 │ # 行为开关
 │ // 这三个从 frontend_screen 配置读取
 ├─ force_direct_context : bool
 ├─ allow_invalid_glx_destroy_window : bool
 ├─ keep_native_window_glx_drawable : bool
 │ // 如果是 DRI3 并且渲染和显示不是同一个 GPU 时，不能
 ├─ can_EXT_texture_from_pixmap : bool
 │
 │ # GLX server strings
 ├─ serverGLXexts : const char* // xcb glXQueryServerString GLX_EXTENSIONS
 ├─ serverGLXvendor : const char*
 ├─ serverGLXversion : const char*
 ├─ effectiveGLXexts : char* // 报告给应用的实际可用扩展串，从交集、override等计算出来
 │
 │ # GLX 扩展跟踪
 ├─ ext_list_first_time : GLboolean
 ├─ direct_support[__GLX_EXT_BYTES]
 │ // frontend_screen 配置 glx_extension_override
 ├─ glx_force_enabled[__GLX_EXT_BYTES]
 ├─ glx_force_disabled[__GLX_EXT_BYTES]
 │ // frontend_screen 配置 indirect_gl_extension_override
 ├─ gl_force_enabled[__GL_EXT_BYTES]
 └─ gl_force_disabled[__GL_EXT_BYTES]
```


> libgallium interface
driCreateNewScreen3 -> `dri_screen`

```
dri_screen
 ├─ base : pipe_frontend_screen // st_api 前端屏幕基类
 │   │ // created from dri_screen.dev, [](#pipe_screen)
 │   ├─ screen : pipe_screen* // d3d12, llvmpipe, softpipe, zink, etc
 │   ├─ 函数指针
 │   └─ st_screen : st_screen* // stack_tracker
 │       ├─ drawable_ht : hash_table *
 │       └─ st_mutex : simple_mtx_t
 ├─ dev : pipe_loader_device * // [](#pipe_loader_device) 创建 pipe_screen
 |
 │ // 传入参数, TBC: 用途
 ├─ fd : int
 ├─ myNum : int
 ├─ type : enum dri_screen_type
 │
 │ # 从capabilities相关推导的字段
 │ // 各 API 最大版本能力，通过编译期对应的常数/支持 + pipe_screen 声明的能力判断一个最大的支持版本，可以用环境变量覆盖
 ├─ max_gl_core_version : int
 ├─ max_gl_compat_version : int
 ├─ max_gl_es1_version : int
 ├─ max_gl_es2_version : int
 ├─ api_mask : unsigned int // 每种API是否支持 (version > 0)
 │ // 特定能力, 从 pipe_screen.caps copy, TBC: why 专门字段？会修改？
 ├─ throttle : bool
 ├─ has_reset_status_query : bool
 ├─ has_protected_context : bool
 ├─ target : enum pipe_texture_target // 主要是framebuffer是否支持 not power of two, 涉及一些 mipmap, coord 区别
 ├─ dmabuf_import : bool
 ├─ has_dmabuf : bool
 │ // 特定能力，非 pipe_screen.caps copy
 ├─ has_multibuffer : bool // glx_display 的 DRI3 present 扩展
 │ # END
 │
 │ # 函数指针
 │ // 由参数 loader_extensions 填充，全是函数指针
 ├─ swrast_loader : __DRIswrastLoaderExtension *
 ├─ kopper_loader : __DRIkopperLoaderExtension *
 ├─ dri2.image : __DRIimageLookupExtension *
 ├─ image.loader : __DRIimageLoaderExtension *
 ├─ mutableRenderBuffer.loader : __DRImutableRenderBufferLoaderExtension *
 │ // 透传 user_data, TBC: 似乎主要是传给上面的函数指针
 ├─ loaderPrivate : void *
 │ # END
 │
 │ # 配置相关
 │ // dri2 的 配置文件
 ├─ optionInfo : driOptionCache
 ├─ optionCache : driOptionCache
 │ // 从上面读取的一个 typed strcture version
 ├─ options : st_config_options
 │ // 特定配置
 ├─ pp_enabled : unsigned[PP_FILTERS] // 调试/抗锯齿后处理参数, > 0 enabled
 ├─ swrast_no_present : bool // 软渲时禁用呈现，似乎是为了mitigate一个bug
 │ # END
 │
 │ # OpenCL
 ├─ opencl_func_mutex : mtx_t
 │ // OpenCL interop 回调
 ├─ opencl_dri_event_add_ref : opencl_dri_event_add_ref_t
 ├─ opencl_dri_event_release : opencl_dri_event_release_t
 ├─ opencl_dri_event_wait : opencl_dri_event_wait_t
 ├─ opencl_dri_event_get_fence : opencl_dri_event_get_fence_t
 │ # END
 │
 │ # MISC
 ├─ screen_extensions : const __DRIextension *[14] // 似乎是忘删的代码 9f461400ebdb8513aa64964ccbc45f868bcd32e5
 ├─ can_share_buffer : bool
 └─ is_sw : bool // kopper/软件栈相关标记

```

#### pipe_screen
主要是 一堆函数指针 和 实现的各种能力限制值
```
pipe_screen
 ├─ refcnt : int
 ├─ winsys_priv : void *
 │
 │ // 能力查询（只读，驱动初始化时填充）
 ├─ caps : const pipe_caps
 ├─ shader_caps : const pipe_shader_caps[MESA_SHADER_MESH_STAGES]
 ├─ compute_caps : const pipe_compute_caps
 ├─ nir_options : const nir_shader_compiler_options *[MESA_SHADER_MESH_STAGES]
 │
 ├─ get_screen_fd() // 返回 screen 关联的 fd（只读，不会 close）
 ├─ num_contexts : unsigned // 原子递增，跟踪上下文数量，用于跳过锁
 ├─ transfer_helper : u_transfer_helper*
 │
 └─ 剩下的全是函数指针
```

这里是一个 gallium driver 分支的地方:
sw_screen 允许下面这些 drivers: llvmpipe, virpipe, softpipe, zink, d3d12

有类似装饰器模式的方式去 hook up 函数调用以调试。


#### pipe_loader_device
pipe_loader_device 主要负责创建 pipe_screen, 需要根据不同的 sw / kopper / kms_dri 等等去创建

drisw (software)
```
pipe_loader_sw_device
 ├─ base : pipe_loader_device
 │   │ // PIPE_LOADER_DEVICE_SOFTWARE (sw) 或 PIPE_LOADER_DEVICE_PLATFORM (kopper/vk)
 │   ├─ type : enum pipe_loader_device_type
 │   ├─ u : union
 │   │   └─ pci : { vendor_id, chip_id } // sw 不使用
 │   ├─ driver_name : char * // swrast(sw) / kopper(vk)
 │   ├─ ops : const pipe_loader_ops * // 函数指针 sw / vk 两套
 │   ├─ option_cache : driOptionCache
 │   └─ option_info : driOptionCache
 │
 ├─ dd : const sw_driver_descriptor *
 │   │   └─ = &driver_descriptors (sw) 或 &kopper_driver_descriptors (vk)
 │   ├─ create_screen : function pointer // vk(sw) 或 zink(vk)
 │   └─ winsys[] : 四种不同 winsys 的工厂函数
 ├─ ws : struct sw_winsys * // 函数指针，根据 winsys 从 dd.winsys 创建
 │   ├─ "dri" : 引用 swrast_loader.get|put_image*
 │   ├─ "kms_dri"
 │   ├─ "null"
 │   └─ "wrapped"
 └─ fd : int // -1 (默认), 仅 kms_dri 路径下通过 os_dupfd_cloexec(fd) 设置
```


## Context
```
glx_context
 ├─ vtable : const glx_context_vtable* // DRI3 / DRISW / indirect
 │ # link
 ├─ psc : glx_screen*
 ├─ config : glx_config*
 ├─ driContext : dri_context*  // (libgallium) 指向 dri_context (DRI3/DRISW) 或 indirect 各自的私有数据
 │
 │ # Drawing command buffer (前6个字段，dummy context 静态初始化依赖顺序)
 ├─ buf : GLubyte*       // 协议渲染命令缓冲区
 ├─ pc : GLubyte*        // 当前写入位置
 ├─ limit : GLubyte*     // 溢出检测边界
 ├─ bufEnd : GLubyte*    // 缓冲区末尾
 ├─ bufSize : GLint      // 缓冲区大小
 │
 │ # X Windows System 相关
 ├─ xid : XID // xcb 获取的 ID
 ├─ share_xid : XID
 ├─ majorOpcode : GLint  // GLX 扩展主操作码
 │
 │ # Context 状态
 ├─ imported : GLboolean         // ImportContext 创建的（server 端由其他 client 创建）
 ├─ currentContextTag : GLXContextTag // MakeCurrent 返回的 server 端标签
 ├─ isDirect : Bool              // 是否 DRI
 ├─ renderType : int             // GLX_RGBA_TYPE 等
 ├─ noError : Bool               // GLX_ARB_create_context_no_error
 │
 │ # 渲染模式 (GL_RENDER / GL_SELECT / GL_FEEDBACK)
 ├─ renderMode : GLenum
 ├─ feedbackBuf : GLfloat*
 ├─ selectBuf : GLuint*
 │
 │ # Client 端状态
 ├─ attributes : __GLXattributeMachine // client 端属性栈
 ├─ error : GLenum                     // client 端错误码
 ├─ client_state_private : void*       // 仅 indirect 渲染使用
 │
 │ # 当前 Drawable
 ├─ currentDpy : Display*          // 当前 display，未 current 时为 NULL
 ├─ currentDrawable : GLXDrawable  // 当前绘制目标
 ├─ currentReadable : GLXDrawable  // 当前读取目标
 │
 │ # GL 实现字符串 (从 server 获取)
 ├─ vendor : GLubyte*
 ├─ renderer : GLubyte*
 ├─ version : GLubyte*
 ├─ extensions : GLubyte*
 │
 │ # Server GL 版本
 ├─ server_major : int
 ├─ server_minor : int
 │
 │ # 其他
 ├─ maxSmallRenderCommandSize : GLint   // min(64k, bufSize)
 └─ gl_extension_bits[__GL_EXT_BYTES]   // GL 扩展跟踪
```

```
libgallium`dri_context
 ├─ st : st_context*        // mesa/st 状态跟踪上下文
 │ # MakeCurrent
 ├─ draw : dri_drawable*    // 绘制目标
 ├─ read : dri_drawable*    // 读取目标
 │ # link
 ├─ screen : dri_screen*
 ├─ loaderPrivate : void*   // loader 透传 user data, glx_context
 │
 ├─ is_shared_buffer_bound : bool // __DRI_IMAGE_BUFFER_SHARED
 │
 │ # dri2 stamp，用于跟踪 drawable 是否需要更新
 ├─ dri2.draw_stamp : int
 ├─ dri2.read_stamp : int
 │
 │ # Misc
 ├─ pp : pp_queue_t* // Mesa 的后处理 dri_screen.pp_enabled
 └─ hud : hud_context* // 类似帧数显示那种叠加层
```

// TODO: 要不要 inline 到 dri_context 中去
```
st_context
 │ # 核心上下文
 ├─ ctx : gl_context*                    // Mesa GL 上下文
 ├─ pipe : pipe_context*                 // Gallium 管线上下文 (命令提交)
 │ # Links
 ├─ screen : pipe_screen*                // Gallium 屏幕 (设备级)
 ├─ cso_context : cso_context*           // CSO 状态缓存
 │
 │ # 配置
 ├─ options : st_config_options           // driconf 配置选项
 │
 │ # 相关资源
 ├─ winsys_buffers : list_head # make current 时候的 drawable 对应的 gl_framebuffer, 见 Note1
 │
 │ # 状态更新机制
 ├─ update_functions[ST_NUM_ATOMS] : st_update_func_t  // 每个 atom 的更新回调 (共 55 种)
 │
 │ # 前端关联
 ├─ frontend_screen : pipe_frontend_screen*  // 例如 dri_screen
 ├─ frontend_context : void*                 // 例如 dri_context
 │
 │ # Draw 模块 (软件回退路径)
 ├─ draw : draw_context*                  // GL_SELECT/GL_FEEDBACK/glRasterPos
 ├─ feedback_stage : draw_stage*          // GL_FEEDBACK 渲染模式
 ├─ selection_stage : draw_stage*         // GL_SELECT 渲染模式
 ├─ rastpos_stage : draw_stage*           // glRasterPos
 │
 │ # 线程调度
 ├─ work_counter : unsigned               // 用于 AMD Zen L3 线程绑定 & 资源修剪
 │
 │ # 能力/特性标志
 ├─ clamp_frag_color_in_shader : GLboolean
 ├─ clamp_vert_color_in_shader : GLboolean
 ├─ thread_scheduler_disabled : bool
 ├─ has_stencil_export : bool             // shader 模板导出
 ├─ has_time_elapsed : bool
 ├─ has_etc1/has_etc2/transcode_etc : bool
 ├─ transcode_astc/has_astc_2d_ldr/has_astc_5x5_ldr : bool
 ├─ has_s3tc/has_rgtc/has_latc/has_bptc : bool   // 纹理压缩格式
 ├─ prefer_blit_based_texture_transfer : bool
 ├─ allow_compute_based_texture_transfer : bool
 ├─ force_compute_based_texture_transfer : bool
 ├─ force_specialized_compute_transfer : bool
 ├─ force_persample_in_shader : bool
 ├─ has_shareable_shaders : bool
 ├─ has_multi_draw_indirect : bool
 ├─ has_occlusion_query : bool
 ├─ has_single_pipe_stat/has_pipeline_stat : bool
 ├─ has_indep_blend_enable/has_indep_blend_func : bool
 ├─ can_dither : bool
 ├─ lower_flatshade/lower_alpha_test : bool       // NIR lowering 标志
 ├─ lower_point_size/add_point_size : bool
 ├─ lower_two_sided_color/lower_ucp : bool
 ├─ lower_rect_tex : bool
 ├─ has_conditional_render : bool
 ├─ allow_st_finalize_nir_twice : bool
 ├─ needs_texcoord_semantic : bool
 ├─ emulate_gl_clamp : bool
 ├─ draw_needs_minmax_index : bool
 ├─ has_hw_atomics : bool
 ├─ validate_all_dirty_states : bool
 ├─ can_null_texture : bool
 ├─ is_threaded_context : bool
 ├─ can_scissor_clear : bool
 ├─ shader_has_one_variant[MESA_SHADER_MESH_STAGES] : bool
 │
 │ # 当前硬件状态 (缓存, 用于脏状态检测)
 ├─ state.blend : pipe_blend_state
 ├─ state.depth_stencil : pipe_depth_stencil_alpha_state
 ├─ state.rasterizer : pipe_rasterizer_state
 ├─ state.vert_samplers[PIPE_MAX_SAMPLERS] : pipe_sampler_state
 ├─ state.frag_samplers[PIPE_MAX_SAMPLERS] : pipe_sampler_state
 ├─ state.num_vert_samplers / num_frag_samplers : GLuint
 ├─ state.num_sampler_views[MESA_SHADER_MESH_STAGES] : GLuint
 ├─ state.num_images[MESA_SHADER_MESH_STAGES] : unsigned
 ├─ state.clip : pipe_clip_state
 ├─ state.constbuf0_enabled_shader_mask : unsigned
 ├─ state.fb_width/fb_height/fb_num_samples/fb_num_layers/fb_num_cb : unsigned
 ├─ state.num_viewports : unsigned
 ├─ state.scissor[PIPE_MAX_VIEWPORTS] : pipe_scissor_state
 ├─ state.viewport[PIPE_MAX_VIEWPORTS] : pipe_viewport_state
 ├─ state.window_rects.{num, include, rects[]} : ...
 ├─ state.poly_stipple[32] : GLuint
 ├─ state.fb_orientation : GLuint
 ├─ state.enable_sample_locations : bool
 ├─ state.sample_locations_samples : unsigned
 └─ state.sample_locations[...] : uint8_t
 │
 │ # 脏状态追踪
 ├─ active_states : st_state_bitset       // BITSET_DECLARE(_, 55), 标记活跃状态
 ├─ active_queries : unsigned             // 当前活跃查询数 (meta op 暂停用)
 │
 │ # 当前绑定的着色器程序 (union 匿名结构体)
 ├─ vp : gl_program*                      // 顶点程序
 ├─ tcp : gl_program*                     // 细分控制程序
 ├─ tep : gl_program*                     // 细分求值程序
 ├─ gp : gl_program*                      // 几何程序
 ├─ fp : gl_program*                      // 片段程序
 ├─ cp : gl_program*                      // 计算程序
 ├─ tp : gl_program*                      // 任务程序 (mesh shading)
 ├─ mp : gl_program*                      // 网格程序 (mesh shading)
 ├─ current_program[MESA_SHADER_MESH_STAGES] : gl_program*  // 同上, 数组访问
 │
 │ # 资源释放
 ├─ release_resources : util_dynarray     // 延迟释放的资源列表
 ├─ release_counter : unsigned
 │
 │ # 着色器变体
 ├─ vp_variant : st_common_variant*       // 当前顶点程序变体
 │
 │ # glDrawPixels / glBitmap / glReadPixels 支持
 ├─ pixel_xfer.pixelmap_texture : pipe_resource*
 ├─ pixel_xfer.pixelmap_sampler_view : pipe_sampler_view*
 │
 ├─ bitmap                                // glBitmap 状态
 │  ├─ rasterizer : pipe_rasterizer_state
 │  ├─ sampler : pipe_sampler_state
 │  ├─ tex_format : pipe_format
 │  └─ cache : st_bitmap_cache            // 位图缓存
 │     ├─ xpos, ypos : GLint
 │     ├─ xmin, ymin, xmax, ymax : GLint
 │     ├─ fp : gl_program*
 │     ├─ color[4] : GLfloat
 │     ├─ zpos : GLfloat
 │     ├─ texture : pipe_resource*
 │     ├─ trans : pipe_transfer*
 │     ├─ empty : GLboolean
 │     └─ buffer : uint8_t*               // I8 纹理图像
 │
 ├─ drawpix.zs_shaders[6] : void*        // glDrawPixels 深度/模板着色器
 │
 ├─ drawpix_cache                         // glDrawPixels 图像缓存
 │  ├─ entries[4] : drawpix_cache_entry
 │  │  ├─ width, height : GLsizei
 │  │  ├─ format, type : GLenum
 │  │  ├─ pixelmaps : gl_pixelmaps
 │  │  ├─ user_pointer : void*
 │  │  ├─ image : void*
 │  │  ├─ texture : pipe_resource*
 │  │  └─ age : unsigned
 │  └─ age : unsigned
 │
 ├─ readpix_cache                         // glReadPixels 缓存
 │  ├─ src : pipe_resource*
 │  ├─ cache : pipe_resource*
 │  ├─ dst_format : pipe_format
 │  ├─ level, layer : unsigned
 │  └─ hits : unsigned
 │
 │ # glClear
 ├─ clear
 │  ├─ raster : pipe_rasterizer_state
 │  ├─ viewport : pipe_viewport_state
 │  ├─ vs / fs : void*
 │  ├─ vs_layered / gs_layered : void*    // 分层渲染
 │
 │ # PBO (Pixel Buffer Object) 纹理上传/下载
 ├─ pbo
 │  ├─ raster : pipe_rasterizer_state
 │  ├─ upload_blend : pipe_blend_state
 │  ├─ vs / gs : void*
 │  ├─ upload_fs[5][2] : void*
 │  ├─ download_fs[5][PIPE_MAX_TEXTURE_TYPES][2] : void*
 │  ├─ shaders : hash_table*
 │  ├─ upload_enabled / download_enabled : bool
 │  ├─ rgba_only / layers / use_gs : bool
 │
 │ # 计算着色器纹理压缩
 ├─ texcompress_compute
 │  ├─ progs : gl_program**
 │  ├─ bc1_endpoint_buf : pipe_resource*
 │  ├─ astc_luts[5] : pipe_sampler_view*
 │  └─ astc_partition_tables : hash_table*
 │
 │ # 工具顶点
 ├─ util_velems : cso_velems_state         // st_util_vertex 的顶点元素
 ├─ passthrough_vs : void*                 // 直通顶点着色器
 ├─ internal_target : pipe_texture_target
 ├─ winsys_drawable_handle : void*
 ├─ uses_user_vertex_buffers : bool
 │
 │ # 着色器资源绑定追踪
 ├─ last_used_atomic_bindings[MESA_SHADER_MESH_STAGES] : unsigned
 ├─ last_num_ssbos[MESA_SHADER_MESH_STAGES] : unsigned
 │
 │ # Drawable stamp
 ├─ draw_stamp : int32_t
 ├─ read_stamp : int32_t
 │
 │ # GPU 重置状态
 ├─ reset_status : pipe_reset_status
 │
 │ # Bindless texture/image
 ├─ bound_texture_handles[MESA_SHADER_MESH_STAGES] : st_bound_handles
 │  ├─ num_handles : unsigned
 │  └─ handles : uint64_t*
 ├─ bound_image_handles[MESA_SHADER_MESH_STAGES] : st_bound_handles
 │
 │ # 内存节流 (限制 in-flight 操作的内存使用)
 ├─ throttle : util_throttle
 │  ├─ ring[10].fence : pipe_fence_handle*
 │  ├─ ring[10].mem_usage : uint64_t
 │  ├─ flush_index / wait_index : unsigned
 │  └─ max_mem_usage : uint64_t
 │
 │ # 僵尸资源回收 (延迟销毁, 线程安全)
 ├─ zombie_sampler_views
 │  ├─ list : st_zombie_sampler_view_node  // 链表头
 │  │  ├─ view : pipe_sampler_view*
 │  │  └─ node : list_head
 │  └─ mutex : simple_mtx_t
 │
 ├─ zombie_shaders
 │  ├─ list : st_zombie_shader_node        // 链表头
 │  │  ├─ shader : void*
 │  │  ├─ type : mesa_shader_stage
 │  │  └─ node : list_head
 │  └─ mutex : simple_mtx_t
 │
 │ # 硬件选择着色器缓存
 └─ hw_select_shaders : hash_table*
```

```
pipe_context
 │ # link
 ├─ screen : pipe_screen* 
 ├─ priv : void* // 透传 user_data, NULL
 │
 ├─ draw : void *
 ├─ vbuf : struct u_vbuf *
 │
 ├─ stream_uploader : struct u_upload_mgr *
 ├─ const_uploader : struct u_upload_mgr *
 │
 ├─ debug : struct util_debug_callback
 └─ 一堆函数指针
```

```
gl_context
 ├─ Shared : gl_shared_state* // 多个context可共享的，比如 compiled shader

```

gl_context vs st_context

| 类型 | gl_context | st_context |
|---|---|---|
| 聚合 | 分散在多个子结构 | 打包成硬件需要的状态块 |
| 条件路径 | 只记录 API 调用 | 根据硬件能力选择实现方式 |
| 坐标变换 | 存用户原始值 | 补偿坐标系差异 |
| 范围限制 | 接受任意值 | clamp 到硬件实际范围 |
| 枚举翻译 | 用 `GL_` 枚举 | 翻译为 `PIPE_` 枚举 | 

make current (from null)
- libgallium
  - set dri_context.draw/read
  - get or create framebuffer for drawable
    - pipe_screen create resource
    - memory malloc
  - TLS _mesa_glapi_tls_Context
  - TLS _mesa_glapi_tls_Dispatch
  - update gl_context info
- TLS __glX_tls_Context
Note: pipe 层是没有 current 的概念，其更像是类函数的， pipe_resource 作为函数参数传入。


glXCreateWindow
```
drisw_drawable
 ├─ base : __GLXDRIdrawable
 │  ├─ destroyDrawable

 │  ├─ xDrawable : XID // X Windows System
 │  ├─ drawable : XID // GLX drawable
 │  ├─ psc : struct glx_screen*

 │  ├─ textureTarget : GLenum
 │  ├─ textureFormat : GLenum
 │  ├─ eventMask : unsigned long
 │  ├─ refcount : int
 │  └─ dri_drawable : struct dri_drawable*
 │ # X Windows System
 ├─ gc : GC
 ├─ xDepth : int
 │
 ├─ config : struct glx_config* // FBConfig
 ├─ ximage : XImage*
 ├─ shminfo : XShmSegmentInfo
 │ # drirc
 └─ swapInterval : int
```

## TDOO
1. TODO: pipe_loader_device.winsys 到底是个什么概念，怎么决定
2. 有几层 driver 的概念？目前看似乎有
   1. __glXInitialize 会有一个 glx_driver: DRI3 / ZINK / SW / WINDOWS
   2. libgallium 创建 pipe_screen 的时候也会有 llvmpipe, virpipe, softpipe, zink, d3d12