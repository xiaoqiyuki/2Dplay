# 第三方组件与授权说明

**这份文件解释"为什么仓库里只有源码，没有打包好的游戏"。**

上传一个 Unity 项目到公开仓库时，最容易出错的地方不是代码，是**把不属于你的东西一起传上去、再挂一个开源协议**。

---

## 一、本仓库里【是】你的东西

| 内容 | 位置 | 授权 |
| --- | --- | --- |
| 全部 C# 脚本 | `Assets/Scripts/`、`Assets/Editor/` | **MIT**（见 LICENSE） |
| 像素画素材 | `Assets/Resources/Art/`、`Assets/Art/` | **MIT** |
| 音效与 BGM | `Assets/Resources/Audio/` | **MIT** |
| 生成的脚本 | `build/*.py` | **MIT** |

**这些全部是程序生成的**（`build/make_tiles.py`、`make_characters.py`、`make_audio.py`），
没有使用任何第三方美术或音频素材，**因此可以放心以 MIT 授权**。

---

## 二、本仓库里【不是】你的东西

### Unity 引擎本身

**Unity 不是开源软件。** 它按 Unity 的条款授权使用。

| 允许 | 不允许 |
| --- | --- |
| 用 Unity 制作游戏 | 把引擎源码或运行库当作自己的东西分发 |
| 分发**打包好的**游戏 | 声称引擎部分适用你的开源协议 |
| 在仓库里放**指向**引擎的引用 | 在仓库里**附带**引擎的二进制 |

**所以本仓库里没有 Unity 的任何二进制文件。** 想运行这个项目，你需要自己装 Unity 6.3 LTS。

### Unity 官方包

`Packages/manifest.json` 里引用了：

```json
"com.unity.render-pipelines.universal"    // URP
"com.unity.inputsystem"                   // 新输入系统
"com.unity.feature.2d"                    // 2D 功能集
```

这些按 **Unity Companion License** 授权 —— **在 Unity 项目里免费用，但不能单独抽出来分发**。

**本仓库只在 manifest 里写了包名和版本号，没有把包的内容复制进来。**
这是正确做法：Unity 打开工程时会自己下载。**不要把这些包的源码提交进仓库。**

### 构建产物里的第三方二进制

`Build/Windows/` 里那些文件 **不属于你，也不能随本仓库一起开源**：

| 文件 | 大小 | 是什么 |
| --- | --- | --- |
| `UnityPlayer.dll` | 35 MB | Unity 运行时 |
| `MonoBleedingEdge/` | — | Unity 附带的 Mono 运行时 |
| `D3D12Core.dll` / `D3D12/` | 4.5 MB | DirectX 12 组件 |
| `unity default resources` | 5.6 MB | Unity 内置资源 |

**Unity 的条款允许你【分发打包好的游戏】（玩家下载来玩），
但不允许把它当作"你开源的内容"重新发布。**

而且 `Build/` 里还有个文件夹叫 **`2Dplay_BurstDebugInformation_DoNotShip`** ——
**名字就是 Unity 自己给的警告。**

### 字体

界面用的是 Unity 内置的 GUI 字体（不随仓库分发）。
**中文能否正常显示取决于运行环境，打包版的实际效果需要自己确认。**

---

## 三、所以正确做法是什么

```
GitHub 仓库  =  源码          （11.7 MB，可直接阅读、编译、学习）
GitHub Release 附件  =  打包好的游戏  （100 MB，给玩家下载）
```

**这个分工不是形式主义，是两边各自的正确用法：**

| | 仓库 | Release |
| --- | --- | --- |
| 用途 | 版本管理、代码审阅 | 分发可执行文件 |
| Git 擅长 | ✓ 文本、差异、历史 | ✗ 二进制会让仓库永久膨胀 |
| 授权含义 | 你的开源协议 | **"我分发这个游戏"**，不改变第三方组件的授权 |

**把 100 MB 的 `UnityPlayer.dll` 提交进 git 有具体代价**：
Git 会保存每个版本的完整副本，**删掉也清不掉历史** —— 仓库从此永远是几十上百 MB，
克隆一次就要下载全部历史。**这是最常见的"把仓库搞坏"的方式之一。**

---

## 四、如果别人想运行这个项目

1. 装 **Unity 6.3 LTS**（6000.3.x），用 **Universal 2D** 模板
2. 克隆本仓库
3. 用 Unity Hub 打开，等它下载 Packages
4. 打开 `Assets/Scenes/SampleScene.unity`，按 Play

**不需要仓库里有任何 Unity 的二进制。**

---

## 五、素材替换

`Assets/Resources/Art/` 和 `Assets/Resources/Audio/` 里的所有内容都是**占位素材**，
由 `build/` 下的 Python 脚本生成。

**换成正式素材时**：保持文件名不变直接替换即可，代码一行都不用改
（文件名就是接口）。替换后的素材授权归于你自己。
