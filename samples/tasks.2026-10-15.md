# Tasks（测试数据 · MD 只读格式）

# 说明：.md 为只读输入，状态/结果由程序写入同目录 sidecar 文件

# tasks.testdata.md.result.json；运行日志在 runs/ 子目录。

# 适合验证「输入文件不可变」的读取链路与 sidecar 回写。

## td-md-001

- instruction: 读取 samples/config.example.json，列出全部一级配置分组的标题

## td-md-002

- instruction: 汇总 docs 目录下的文档名并按主题分组

## td-md-003

- instruction: 在 samples/ 下生成一个空的 .gitkeep 占位文件
