---
title: About the weird statistics method of named pipe limitation in Azure App Service sandbox
category: tech
tag: Azure App Service, named pipe
---

## Introduction
As you may be aware, Azure App Service on Windows run inside a sandbox environment, which functions similarly to container in order to restrict access to system resources. One of the limitations imposed by the sandbox is the maximum number of named pipes allowed. [It is said](https://github.com/projectkudu/kudu/wiki/Azure-Web-App-sandbox#per-sandbox-per-appper-site-numerical-limits) that only 128 named pipes can be created within a sandbox. However, the behavior of this named pipe limitation is quite strange, or even, puzzling.

Since the source code for implementing this named pipe limitation is closed, I could only provide you with insights into certain behaviors that reflect some nature of this limitation and why it may be considered problematic.


### Get named pipe count
To analyze the number of named pipe, the first question is to obtain the number of named pipes in a sandbox?
The answer is quite simple: by querying a specific environment variable called `"WEBSITE_COUNTERS_APP"`.
This environment variable is a special one, it yields a JSON object, which containing a field named "namedPipes". By querying this environment variable, you can obtain the up-to-date count of named pipes in the sandbox.


### Named Pipe
- `CreateNamedPipe` without `CreateFile`
```c++
CreateNamedPipe // cnt += 1
CloseHandle // without CreateFile, this will not dec cnt
```
Even after closing the named pipe handle, the count remains unchanged.

- Limited MaxInstances
```c++
hPipe = CreateNamedPipe(..., nMaxInstances = 1, ...) // cnt += 1
hFile = CreateFile // cnt -= 1
CloseHandle(/* hFile */) // no change
CloseHandle(/* hPipe */) // no change
```
You might expect the count to be decrease only after you `CloseHandle` of the pipe handle. However, in reality, the count decreases immediately after all instances are used by `CreateFile`.

- Unlimited MaxInstances
```c++
CreateNamedPipe(..., nMaxInstances = PIPE_UNLIMITED_INSTANCES, ...) // cnt += 1
hFile = CreateFile // no change
CloseHandle(/* hFile */) // no change
CloseHandle(/* hPipe */) // no change
```
Even if you have closed the handle of named pipe, the count remains unaffected.

- Re-create a named pipe with same name of an unlimited one
```c++
CreateNamedPipe(lpName = /* same as the one with unlimited instances */, ...) // no change
hFile = CreateFile // no change
CloseHandle(/* hFile */) // no change
CloseHandle(/* hPipe */) // no change
```
After you have closed the handle of an unlimited one, the budget is wasted (because the count does not decrease). But you can recreate a named pipe with the same name without increase the count.
But you still cannot decrease that value by close the new handle either.

- After process is terminated, the one leaked by unlimited & no-CreateFile is also released. But I haven't tested what happens when you pass the named pipe between processes, such as creating a named pipe in process A, passing to process B, then kill process A.


### Anonymous Pipe
> Anonymous pipes are implemented using a named pipe with a unique name.
> -- [Anonymous Pipe Operations](https://learn.microsoft.com/en-us/windows/win32/ipc/anonymous-pipe-operations)

```c++
CreatePipe // no change
```
Despite anonymous pipes being based on named pipe, they are not counted.
One possible explanation is that anonymous pipe implemented by a named pipe with maxInstance = 1, and it internally calls `CreateFile`, causing the named pipe count immediate decrease after increase by 1, which makes the counter unchanged.


### A brief conclusion
Perhaps it is rather difficult to correctly count the number, or maybe it is a feature. But personally, I find the behavior to be counterintuitive compared to the normal behavior of named pipes.
However, to some extent, it has its advantages, as it allows you to bypass the 128 named pipe limitation with limited `nMaxInstances`. While 128 is not a small number, this limitation still applies to Basic+, where you have unlimited threads/processes/connections. But you should avoid creating named pipes without `CreateFile` to avoid leak the counter.
