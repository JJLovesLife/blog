```
gl_context
|- PopAttribState
|   |- mask of glClearColor/Depth/Stencil
|   |- glScissor
|   |- glEnable
|- NewState // TODO
|   |- New buffer glBindFramebuffer
|- NewDriverState // TODO
|   |- glClearDepth 和 depth test 相关的，但是clear本身没有
|   |- glScissor 所有的API，包括 glWindowRectanglesEXT
|   |- ST_NEW_RASTERIZER (glEnable)
| # 能力限制
|- Const // 这个 context 实现的限制常数
|- Extensions // 是否支持扩展


gl_colorbuffer_attrib
|- ClearIndex : int <- glClearIndex (Mesa已不支持，仅保留字段)
|- ClearColor : float/int <- glClearColor | glClearColorIiEXT | glClearColorIuiEXT

Depth : gl_depthbuffer_attrib
|- Clear : float <- glClearDepth
| # depth test 相关的
|- Test <- glEnable
|- Func : enum <- glDepthFunc
|- Mask : bool <- glDepthMask
|- BoundsTest <- glEnable
|- BoundsMin/Max : float <- glDepthBoundsEXT

gl_stencil_attrib
|- Clear <- glClearStencil

Scissor : gl_scissor_attrib
|- EnableFlags : bit mask <- glEnable, per viewport
|- ScissorArray[16](x/y/w/h) <- glScissorXXX
|- WindowRects/NumWindowRects/WindowRectMode <- glWindowRectanglesEXT glScissor 增强版，多个矩形的并集

Texture : gl_texture_attrib
|- CurrentUnit
|- Unit[]
|   |- CurrentTex[] // 1D, 2D, 3D, CUBE ... 每种类型一个
|   |- _BoundTextures : bitmask // 已绑定的非默认 texture
|- ProxyTexture[] // 每种类型一个 // TODO: 什么是 proxy

Pack, Unpack : gl_pixelstore_attrib
|- xxx <- glPixelStore[if]
X- BufferObj // glBindBuffer(GL_PIXEL_PACK_BUFFER
```

### Compatibility Profile
gl_context
|- _ImageTransferState : bitmask // calc <- glPixelTransfer

```
Pixel : gl_pixel_attrib
|- ReadBuffer
|- xxx <- glPixelTransfer
X- ZoomX/Y

PixelMaps : gl_pixelmaps <- glPixelMap
```

## Frame buffer
frame buffer 类似一个有插槽的容器，需要插上 render buffer 之后才能使用。
'_' 开头的字段是内部维护的字段，不直接对应到 OpenGL API
```
gl_framebuffer
|- ColorDrawBuffer[] <- glDrawBuffer | glNamedFramebufferDrawBuffer
|- _NumColorDrawBuffers
|- _ColorDrawBufferIndexes[] // ColorDrawBuffer 映射到内部 enum
|
|- ColorReadBuffer <- glReadBuffer | glNamedFramebufferReadBuffer
|- _ColorReadBufferIndex // ColorReadBuffer 映射到内部 enum
|
|- Visual <- 变换自 glXGetFBConfigs
```


//TODO: 一些散布在各个地方的状态维护逻辑：
- FLUSH_VERTICES
- ST_SET_STATE