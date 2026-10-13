# FreeWorldTalk 源码协作

- 本目录是独立 Harmony Mod 仓库。游戏工程仅提供编译引用，不能把游戏或 Unity DLL、游戏资源和玩家运行数据提交到本仓库。
- 功能或配置变化时，同步维护根目录 `README.md` 与 `docs/images/` 中受影响的 SVG。图中流程、默认值与功能边界必须与源码一致，并在 README 提供对应文字说明。
- SVG 使用本地相对引用，不嵌入脚本、外部资源或私人数据。使用冷色、扁平风格，保证标题与说明易读。
- 根目录 README、DEPENDENCIES 与 SVG 是文档源；`package/README.md`、`package/DEPENDENCIES.md` 和 `package/docs/` 是构建生成的副本，不手工维护、不提交。
- `package/Manifest/` 和 `package/Prompts/` 保存可分发模板。不要把安装目录里玩家修改过的提示词、API Key、角色 JSON、记忆、缓存或存档复制到这些模板。
- 构建使用 `build.ps1 -ProjectPath <Unity 工程目录>`；`-NoDeploy` 仅打包。编译和打包不代表 API 或 Play Mode 验收已完成。
- 提交与推送只在用户明确要求时执行。提交前检查暂存范围，保留 `.gitignore` 对密钥、私人配置、依赖与编译产物的排除。
