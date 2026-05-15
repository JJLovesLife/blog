

## GL Context Structure
有多个 face, 多个 mipmap level, 每一个对应一个 `gl_texture_image`
```
gl_texture_object
|- Target : enum <- glBindTexture | glCreateTextures // 类型
|   |- TargetIndex // 从 Target 的 enum 映射到 array index, 从而便捷的访问 .Texture.Unit.CurrentTex 这些数组
|- Image[cube face:6][mipmap level] : gl_texture_image
|- Sampler
|   |- Attib
|   |   |- Wrap, Min/MagFilter
| # ext:OES_EGL_image_external
|- External
|- lastLevel // 应该是 [0, lastLevel] 对应的 pipe_resource 已经创建，因为 GL API 的设计方式，level 的创建是可以延迟的。
|- pt : pipe_resource // 见 section pipe_resource

gl_texture_image
|- pt : pipe_resource // 见 section pipe_resource
|- TexObject, Face, Level # back_ptr
|
|- InternalFormat <- glTexImage (user)
|- _BaseFormat : enum // 去除了 bit / order / sRGB / compress 信息的 base 格式, _mesa_base_tex_format
|- TexFormat // mesa_format 内部 format, 用一通很复杂的逻辑计算出来，沉重的历史包袱
|
|- Width, Height, Depth, Border <- glTexImage
|- MaxNumLevels // max mipmap level
|
|- NumSamples
|- FixedSampleLocations
|
|- FormatSwizzle // TODO: 用途
|
|- transfer[]
X- compressed_data : st_compressed_data
```

### pipe_resource

`gl_texture_object` 和 `gl_texture_image` 都有一个 `pipe_resource` 字段。这里 object 上面的是一个所有 mipmap 一起分配的 resource （以 softpipe 为例，就是一次 malloc 分配的连续空间）。
但是因为 OpenGL 每个 mipmap 可以是不同 format （至少 glTexImage 的时候可以，能不能用在 shader 另说），所以 image 可能不能直接存到 object 分配的 resource 的对应的 mipmap level 上去。这种时候就会单独分配一个 resource 在 image 上，不然就直接引用 object 的 resource.

这里的 `pipe_resource` 在 softpipe 下就会包括一个 malloc 的内存。

## API
### glTexImage

- `internalformat` 参数只是声明应用希望的格式，而最终的格式由驱动选择。选择的逻辑有非常大的历史包袱，很多兼容性的回退逻辑。（希望Vulkan里面驱动不需要处理这些回退逻辑）
- `internalformat` 对于 `glTexImage` 也可以是压缩格式, 只要该压缩格式支持 online 压缩，即传输的时候去压缩。 // TODO: double check

参数校验
- target
  `legal_teximage_target` (维度 + valid enum + ext)
- internalFormat (valid enum + ext)
  `_mesa_base_tex_format`
- format
  `valid_texture_format_enum` (valid enum + ext)
- type
  `valid_texture_type_enum` (valid enum + ext)
- border == 0
- level, width, height, depth
  合理的maxium支持上限 + 一定程度的内存大小检查
- internalFormat <-> format
- internalFormat <-> type
- internalFormat <-> target
- format <-> type
  是否是合理的枚举对
- 如果是 PBO 的话，检查大小是否在范围内
