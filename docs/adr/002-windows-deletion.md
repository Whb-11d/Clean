# ADR-002：Windows 删除边界

2026-10-28，采纳。永久删除使用 CreateFile(DELETE | FILE_READ_ATTRIBUTES, share=0) 与 FileDispositionInfo。逐级打开祖先目录且不共享删除，检查 FileAttributeTag、最终路径与扫描身份，拒绝多硬链接、重解析点和系统保护位置。文件使用中则失败，不强制处理。

回收站使用 Windows IFileOperation 与 FOFX_RECYCLEONDELETE / FOFX_EARLYFAILURE；单项 PreDeleteItem 要求回收站标志，否则中止，禁止回退永久删除。PostDeleteItem 记录实际结果。Shell 没有接受已打开文件句柄的回收 API，因此最后叶文件检查到 Shell 操作间仍有 TOCTOU 窗口。保留文件句柄（允许删除、不允许写入）和目录锁缩小窗口，并在回调再次比对；不宣称可以防御同用户恶意替换。改用私有暂存目录会影响回收站原路径恢复和崩溃恢复，v0.1 不采用。该残余限制必须进入测试报告和发布已知问题。

官方依据：
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem

清空真实回收站只允许用户交互；自动化使用测试文件回收但不清空全局回收站。
