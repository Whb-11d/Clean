# 第三方组件

Clean 自有代码采用 MIT（见 LICENSE）。精确的 NuGet 依赖图保存在各项目 packages.lock.json。

运行时依赖：

| 组件 | 版本 | 许可证与来源 |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT — https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT — https://github.com/dotnet/runtime |
| .NET / Windows Desktop Runtime | 10.0.14（随 SDK 10.0.403 发布） | MIT 及附带第三方声明 — https://github.com/dotnet/runtime 、https://github.com/dotnet/wpf |

构建/测试依赖（不随应用运行包发布）：Microsoft.NET.Test.Sdk / testhost / CodeCoverage 17.14.1（MIT）、xunit 2.9.3（Apache-2.0）、xunit.runner.visualstudio 2.8.2（MIT）、xunit.analyzers（Apache-2.0）、Microsoft.TestPlatform.ObjectModel（MIT）、Newtonsoft.Json（MIT）、System.Reflection.Metadata（MIT）。各包原始许可证随 NuGet 包提供。

发布 ZIP 应保留运行时自带的 ThirdPartyNotices.txt。NuGet 运行依赖的原始 LICENSE 文件由发布准备阶段复制到 licenses/。更新依赖或 SDK 后需要重新核查本表，不能假定此清单自动同步。
