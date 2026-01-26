---
title: ELF 文件结构速查
---

注1：这里只是简单描述 ELF 文件最核心的结构，作为 cheat sheet 以便于快速查看。不会展开具体的字节描述，有必要请查看 [Wikipedia](https://en.wikipedia.org/wiki/Executable_and_Linkable_Format)。

注2：本文只包括常见模式，可能存在特殊情况。且目前只验证了一个 .so 文件

注3：一个在线 ELF viewer, 随便找的，不保证质量。 [elfy.io](https://elfy.io/)

## 文件本身的结构

一个 ELF 文件由下面几个部分组成：
1. 三个 header: ELF header, program header table, section header table.
2. sections, 每个 section 用于一个目的。
3. 用于满足 align 要求的 section 之间的垃圾字节。

--- MORE ---

section 描述的是文件的静态结构。同时也意味着section header table可能不存在，如果只在 runtime 使用。

有两个特殊的 section:
1. #0 的 empty section
2. 在物理文件上不占用空间的 .bss 段，但是 section header 仍需要描述其相对位置。

## 文件怎么映射到运行时内存 + 运行时数据
由 program header table 描述，又称为 segment。

连续的 sections 会组成一个 `PT_LOAD` segment 指明了物理文件内容怎么映射到虚拟地址上.
存在 sections 不属于任意一个 `PT_LOAD`, 比如 section 元数据、调试信息等。

Q: 为什么需要非 `PT_LOAD` segments？
A: 因为 section header 不一定存在，而动态链接器需要一些信息去处理链接、重定位。这些信息就是非 `PT_LOAD` segments 所描述的。

## 具体 section

元信息：`SHT_Node`, 对应结构体 `Elf64_Nhdr`
- `.note.gnu.property`
  一些硬件架构信息，比如是否支持间接分支跟踪（Indirect Branch Tracking）、影子栈（Shadow Stack）。
  对应的 `Elf64_Nhdr.n_type`: NT_GNU_PROPERTY_TYPE_0
- `.note.gnu.build-id`
  build ID 一个哈希值
  对应的 `Elf64_Nhdr.n_type`: NT_GNU_BUILD_ID
- `.dynamic`:
  运行时需要的 section metadata 的复制，被动态链接器使用。
- `.gnu_debuglink`:
  调试文件名称+CRC32

对外符号信息：
符号名+hash+版本
- `.gnu.hash`
  `SHT_GNU_HASH`, 对应结构体 `Elf_GNU_Hash_Header`
  一个带布隆过滤器的 hash 表，检查一个 string 是否在 .dynsym 中
- `.dynsym`
  `SHT_DYNSYM`, 对应结构体 `Elf64_Sym[]`
  定义了各种 symbol, 包括引用的外部符号、此文件暴露给外部的符号
- `.dynstr`
  `SHT_STRTAB`, 多个 `'\0'` 结尾的字符串，第一个字符串一定是空字符串，即第一个字节为 `'\0'`
  被 `.dynsym` 引用作为函数名。
  被 `.gnu.version_r` 引用作为版本名。
  被 `.dynamic` 引用作为依赖模块名。
- `.gnu.version`
  给 `.dynsym` 中的每一个 entry 增加了一个版本, 版本是 idx, 索引到 `.gnu.version_r` 或者 `.gnu.version_d`。
  这个 section 是作为对于 `Elf64_Sym.st_info` 的 BIND 字段的扩展而引入的，为了解决库定义的函数因为版本更新带来不兼容改变的情况。
- `.gnu.version_r`
  引用的符号的版本，每个版本是一个 file (str) + name (str)

重定向相关信息：
- `.rela.dyn`
  描述如何进行重定向。通常需要重定向的包括：
  1. 本模块的绝对地址，比如全局变量中取地址。 通常包括如下 sections: `.init_array`, `.fini_array`, `.data.rel.ro`, 部分 `.data` 等。
  1. 引用的外部符号的 .got 段。
- `.rela.plt`
  和 `rela.dyn` 一样，但是描述的是 `.got.plt` section 中的重定向。可能会出现延迟绑定。
- `.got`
  需要重定向的数据符号，或者weak func之类的。
- `.got.plt`
  前三个分别是：
  1. `.dynamic`的地址
  2. 由动态链接器创建的本模块的 `link_map` 结构体。
  3. 动态链接器提供的 lazy binding 的函数，比如 `_dl_runtime_resolve`.

  后面的时引用的外部符号的重定向之后的地址，被`.plt.sec`引用来调用外部函数。使用 lazy binding 的时候，初始化为 `.plt + 0`。Binding 之后，指向外部符号的绝对地址。

可执行代码：
- `.init`, `.init_array`
  初始化代码
- `.fini`, `.fini_array`
  析构代码
- `.plt`
  调用外部符号的跳板代码。`.plt`用于lazy binding的第一次调用。因为IBT的安全要求，现代的binary会把曾经一起放在`.plt`的第一次调用和后续调用指令分成 `.plt` 和 `.plt.sec` 两个 section。
  `.plt` 的代码大概如下：
  - PLT0，这个负责转发PC到 `_dl_runtime_resolve` 之类的动态链接库提供的lazy binding.
    ```asm
    push .got.plt+4 # link_map
    jmp *(.got.plt+8) # _dl_runtime_resolve
    ```
  - PLTN (N >= 1), 负责第一次 lazy binding, 转发到 PLT0, `_idx` 是 `.rela.plt` 的索引。
    ```asm
    endbr64
    push _idx
    jmp PLT0
    ```
  `.plt.got`, `.plt.sec` 实际的 PLT 跳板代码。`.plt.got` 用于那些 weak func, 不走 lazy binding, 而 `.plt.sec` 是 binding 之后跳转代码。代码通常如下：
    ```asm
    .text
    call .plt.sec + X

    .plt.sec:
    endbr64
    jmp *(.got.plt+X)
    ```
- `.text` 程序代码

异常相关：
- `.eh_frame_hdr`, `.eh_frame`: 略

程序数据：
- `.data.rel.ro`, `.data`:
  非零数据（整个结构体非零），`.data.rel.ro`是需要重定向的只读数据。
  `.data` 则是其他，包括需要重定向的可写数据。
- `.bss`
  初始化为零的数据。

## 示例
```
Load Segment 0:
[0, 0x40)          ELF header
[0x40, 0x2A8)      program header table
[0x2A8, 0x2C8)     .note.gnu.property
[0x2C8, 0x2EC)     .note.gnu.build-id
[0x2EC, 0x2F0)     (4)
[0x2F0, 0x694C)    .gnu.hash
[0x694C, 0x6950)   (4)
[0x6950, 0x1B068)  .dynsym
[0x1B068, 0x2D2FD) .dynstr
[0x2D2FD, 0x2D2FE) (1)
[0x2D2FE, 0x2EE40) .gnu.version
[0x2EE40, 0x2EE70) .gnu.version_r
[0x2EE70, 0x428C8) .rela.dyn
[0x428C8, 0x429E8) .rela.plt
[0x429E8, 0x43000) (0x618)

Load Segment 1:
[0x43000, 0x4301B) .init
[0x4301B, 0x43020) (5)
[0x43020, 0x430F0) .plt
[0x430F0, 0x43100) .plt.got
[0x43100, 0x431C0) .plt.sec
[0x431C0, 0x45F11) .text
[0x45F11, 0x46000) (0xEF)
[0x46000, 0x61000) wtext
[0x61000, 0x6100D) .fini
[0x6100D, 0x62000) (0xFF3)

Load Segment 2:
[0x62000, 0x7435F) .rodata
[0x7435F, 0x74360) (1)
[0x74360, 0x7481C) .eh_frame_hdr
[0x7481C, 0x74820) (4)
[0x74820, 0x76438) .eh_frame
[0x76438, 0x76ce0) (0x8A8)

Load Segment 3:
[0x76ce0, 0x76CF0) .init_array
[0x76CF0, 0x76D00) .fini_array
[0x76D00, 0x83D98) .data.rel.ro
[0x83D98, 0x83FB8) .dynamic
[0x83FB8, 0x83FF0) .got
[0x83FF0, 0x84000) (0x10)
[0x84000, 0x84078) .got.plt
[0x84078, 0x84084) .data
[0x84084, 0x84084) .bss

Not Load Segment 4:
[0x84084, 0x840B8) .gnu_debuglink
[0x840B8, 0x841CE) .shstrtab
[0x841CE, 0x841D0) (2)
[0x841D0, 0x84950) section header table
File End
```
