---
title: 读书笔记：关于AAD背后
---
本篇文章是关于 "Modern Authentication with Azure Active Directory for Web Applications" 的读书笔记。

感觉这本书比我看过的很多技术书籍相比优秀的一点是第二章很好的从历史角度讲解了一些协议变化的原因。

- Active Directory vs Azure Active Directory
作为并没有使用Active Directory开发过项目的人，我对于Active Directoy可以说是完全不太清楚。但根据书上说的，Active Directory是需要你的电脑是domain joined的，也就是说auth发生在加入domain的时候。另外Active Directory更多的是一个local network的协议，而且需要本地网络基础的支持，对于跨network的auth就无法做到了。
- 保存在数据库里面的用户信息就attribute，当IdP签名之后就成为claim
- OAuth vs claim-based identity protocol
（Figure 2-7）差异在于不仅仅是认证用户，而允许A使用token去代表A向B进行一些操作（获取关注列表之类的）。
- ID token
看去上是因为OAuth的流程中AS返回的code是完全不透明的，所以无法用来验证身份，所以额外返回一个ID token用来同时验证身份
- OAuth2 没有规定很多细节，比如token格式等，导致不同IdP实现差异较大。


名词对比
- managed tenants vs federated tenants
这个应该是和那些从on-premise迁移过来的AAD有关，如果authn在on-premise发生（通过federate的方式），称为federated tenants，否则称为managed tenants。


`https://login.microsoftonline.com/` 之后的 `<tenant>`, `common`, `organizations`, `consumers` 的区别：[Authority](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-application-configuration#authority)

## Entra ID
Every entity that can be authenticated is represented by a principal.
- Application entity
AAD推出这个原因是service principal要求创建权限高，不便于在cloud场景下使用。Application作为一种类似有一定限制的service principal template提供更方便的注册。
按书里的说法，当tenant B的用户访问A中的application时候，如果同意，可以使用A中的application作为模板在B中创建一个service principal
oauth2PermissionGrants 这个表记录了 App 被授权的记录，现在还有这么表吗？每个用户+app都有一条记录？

- 如何query一个object by ID?
```
POST https://graph.microsoft.com/v1.0/directoryObjects/getByIds
Content-type: application/json
Authorization: Bearer $(az account get-access-token --resource https://graph.microsoft.com)
-d '{ "ids": [ "<id1>", ... ]}'
```

### Enterprise Application & App Registrations
Enterprise Application 应该就是（至少普通情况下） service principal, 而 App Registrations 就是 AAD 里面的 Application object.
当一个 tenant 使用一个 Application 的时候，会使用 Application object 作为 template 在目标 tenant 下创建一个 service prinicapl.

当用户第一次使用该 App 的时候，会需要用户的 consent, 获取之后会给用户一个 AppRoleAssignment, 也就是 portal 上面 Enterprise Application 下面的 "Users and groups" blade.
同时在 oauth2PermissionGrants 表中也会出现一个对应的授权记录。

- App Registrations | App roles
这里添加的 role 至少会自动同步到同一个 tenant 的 Enterprise Application 上去。会不会同步到其它 tenant 需要测试。
这里添加的这个 __value__ 会出现在 id_token 的 roles 里面。
Q: 所以这个 role 并不控制任何 AAD 的权限，仅仅是让 App 去自己区分，作为 App 自身的权限管理的一种方式？
A: 大概率是这样的。
- object oauth2Permissions prop
声明了其他 client 可以获取这个 app 的哪种权限。
Q: 和 appRoles 的区别在哪里？

## OAuth
- resource ID
就是 Application object 的 Applicatin ID URI. 但好像只要是 service principal 里面的 servicePrincipalNames 中的任意一个就行，或者是 union of Application's identifierUris + Application's appId. （简单的测试显示后两者是相同的，比第一个多一个 appId）

## OpenId Connect
- Hybrid flow
在用户向AS登录和发送code到服务A的过程中加入ID token
- Authorization code flow
在服务A和服务B交流的过程中处理ID token
- Discovery
`/.well-known/openid-configuration`
- 为什么有多个key？
我之前以为的是为了分布式之类的。但更准确的原因可能是为了key的rotation。
- redirection
不能完全确定，但是应该是说虽然OpenID支持redirection_url，但是AAD的实现限制这些url必须要提前注册在AAD上。作为更加严格的限制。
- id_token
id_token 是你作为一个 app 时你的 client 的身份，一个值得注意的点是这个身份是和 app 相关联的，它会包括这个 client 对于你这个 app 所拥有的 role, 而这个 role 是和 app 强相关的。

## scenario
1. Web App 获取访问用户身份
OpenID Connect ID token, 包括用户在 App role.
2. Web App 使用登录用户身份获取 access_token
OAuth Authorization Code + exchange from AS
Note: 需要 cert or secret
3. Web App 以自己的身份获取 access_token
直接用 token endpoint, grant_type=client_credentials.
权限配置是在 App registration 的 API permissions 配置。（待确定）
4. Web App 使用非登录用户身份获取 access_token
比较复杂，尚未研究
5. 用户直接获取一个 App registration 的 access_token
need find official document


待整理
---
- AAD中的 requiredResourceAccess, 这里应该声明了服务A想要获得的权限。
- admin consent 当一个服务需要用户的某些权限时会跳出提示向用户索要权限，默认来说每个用户第一次使用都需要授权，但是通过admin consent可以只进行一次授权。通过增加`prompt=admin_consent`这个query string进行。这种属于delegated的权限。
对于一个服务需要的权限，在json中包括在`requiredResourceAccess`中，在portal上显示在App reg的API permissions中。
相关的授权记录记录在AAD的`oauth2PermissionGrants`表中。
- delegated permissions
这个权限是从用户那里delegated过去的，如果用户没有这个权限，app也不会有这个权限。

TOCHECK
---
- Enterprise applications vs App registrations
Enterprise applications似乎就是service prinicipal，query objectID返回的type就是servicePrincipal。
而App registrations则是一个template用来在用户consent之后创建service principal。
- AppRoleAssignment 和 oauth2PermissionGrants 之间的关系如何
可能的一个区别是 AppRoleAssignment 中包括了 role? 然后 role 是可以包括 group 和 service principal 的。
看上去这个区别应该是说 AppRoleAssignment 只是给这个 App 用的，单纯的一个 assign 给用户的 string value，控制的在于用户有什么身份。
而 oauth2PermissionGrants 则是实实在在的 AAD 的权限，能用来在 AAD 中做一些事情，比如和其它 client 交互啊之类的。
- 书中 Registering the app in Azure AD 指的是哪一个，其中的 Native/Web 在现在是指哪一个选项


已测试
---
开启Easy Auth之后，访问就会出现一个consent page
![Consent](/img/aad-consent.png)
```
junjieshao@outlook.com
__Permissions requested__
jjs-azfuncdemo
App info
__This application is not published by Microsoft.__
This app would like to:
- View your basic profile
Allows the app to see your basic profile (e.g., name, picture, user name, email address)

This is a permission requested to access your data in Default Directory.
- Maintain access to data you have given it access to
Allows the app to see and update the data you gave it access to, even when you are not currently using the app. This does not give the app any additional permissions.

This is a permission requested to access your data in Default Directory.

[ ] Consent on behalf of your organization

Accepting these permissions means that you allow this app to use your data as specified in their terms of service and privacy statement. You can change these permissions at https://myapps.microsoft.com.

Only accept if you trust the publisher and if you selected this app from a store or website you trust. Microsoft is not involved in licensing this app to you. Hide details
```
Consent 之后 AppRoleAssignment 里面就增加一个项，不管你 appRoleAssignmentRequired 的值是什么。


待测试
---


- https://learn.microsoft.com/en-us/graph/aad-advanced-queries
