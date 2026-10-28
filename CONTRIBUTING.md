# 参与开发

需要 Windows 11 x64 和 global.json 指定的 .NET 10 SDK。执行 `pwsh ./scripts/build.ps1` 构建和测试，`pwsh ./scripts/build.ps1 -Benchmark` 包含十万文件性能测试。

先更新 docs/specs 的需求和验收条件，再修改代码；保留 Core / Windows / App 依赖方向。涉及真实删除的测试必须只操作自己创建的专用测试目录，绝不清空开发者回收站。测试中产生的微小 `Clean-recycle-test-*.txt` 可在回收站中手动处理。

提交前检查 Release 构建、测试和 `git diff --check`；说明关联需求、验证证据及限制。不要提交 .tools、artifacts、机器路径、配置、日志或文件清单。新增依赖需更新锁文件和 THIRD-PARTY-NOTICES.md，并核查漏洞与许可。

本仓库尚未配置远程地址；正式公开后通过目标仓库 issue/PR 协作。
