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

发布 ZIP 的 licenses/ 保留 CommunityToolkit、.NET Runtime、WPF 原始许可及运行时/DI 的第三方声明。DI 的 MIT 许可与 .NET Runtime 同源。更新依赖或 SDK 后需要重新核查本表，不能假定此清单自动同步。
