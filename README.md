# 铲子 · 位置（ShovelPosition）

把战斗界面上的**铲子按钮**做成**位置可调**：九宫格锚点，或「自定义」百分比坐标（X / Y 滑动条）。
默认「自动（原位置）」= 完全不动，保持游戏原生位置。

- mod id：`shovelposition`　name：`铲子 · 位置`　version：`1.0.0`
- 程序集身份：`JTYShovelPosition`（包内固定路径 `Runtime/ModAssembly.dll`）
- 入口类型：`ShovelPositionEntry`
- 实现基线：**照抄「手套」Mod（Glove v1.0.6）的位置设置方案**（同一套 `landMode`/`landX`/`landY` +
  共享组件 `PosEditorRow` + 同一套「搬出容器再按百分比定位」做法）

## 功能

| 功能 | 说明 |
|---|---|
| 位置设置（一套） | `landMode`（`auto` / 九宫格 `tl…br` / `custom`）+ `landX` / `landY`（int 百分比，0–100）。`auto` = 保持游戏原位置；其余 = 覆盖位置。横屏竖屏通用。 |
| 战斗设置页入口 | Mod 按钮列（`ModCol4`，与其他 Mod 入口同列）注入「铲子·设置」→ 打开**本 Mod 独占**浮层面板。 |
| 面板两行 | 第一行「位置」（共享组件 `PosEditorRow.PanelSpec`，复用 `mods/PosEditorRow.cs`）；第二行「启用 · 位置调整」（CheckBox，**本 Mod 自己往 Rows 容器追加的原生控件**，共享组件一个字节没改）。 |
| 即时生效 + 落盘 | 任何改动当帧写入配置，下一帧定位就用新值；`godot.log` 打 `[ShovelPos] 配置已落盘：<键>=<值>`（**不受 `enableLog` 门控**，用于玩家自证）。 |
| 总开关 `enabled` | 关 = 立刻把铲子搬回游戏原生位置，之后完全不动。 |
| 铲子手感零改动 | 只改「按钮摆在哪」，不改铲子本身的判定：点击、快捷键（`Shovel` 动作）、选中高亮、铲除事件、地图上跟随鼠标的铲子图标全部是宿主原逻辑。 |
| 收款码 | 每帧 `SponsorQrInjector.Tick(tree, "铲子 · 位置", PackageRoot, "Resources/SponsorQR.png")`；`aboutAuthor` 首行以 `铲子 · 位置` 逐字开头（注入器靠它判定归属）。 |

## 游戏侧证据（全部实查 `unpack/` = 0.32.0.0）

### 1. 铲子按钮是谁

| 事实 | 证据 |
|---|---|
| 铲子 Feature 注册名 `"Shovel"`，依赖 `Map` / `PacketPick` | `unpack/Registry/Battle/TowerDefenseBattleRegistry.cs:33` / `:72` |
| 宿主类 `ShovelManager : Control`（**控件本身就是那个 70×72 的「铲子底座 + 图标」**） | `unpack/Registry/Battle/Feature/Shovel/ShovelManager/ShovelManager.cs:6` |
| 按钮节点 `%ShovelButton`（TextureButton，贴图 `ShovelBank.png`）+ 子节点 `%ShovelSprite` | `ShovelManager.cs:73-74`；`ShovelManager.tscn:14-27` |
| Feature 上的公开字段 `public ShovelManager shovelManager` | `unpack/Registry/Battle/Feature/Shovel/TowerDefenseBattleFeatureShovel.cs:10` |
| 另一条取用路径 `TowerDefenseBattleFeatureMap.shovelManager` | `unpack/Registry/Battle/Feature/Map/TowerDefenseBattleFeatureMap.cs:100` |

### 2. 位置是 **prefab 写死** 还是 **运行时算的**？→ **运行时算的**

- `ShovelManager.tscn` 只给了尺寸：`custom_minimum_size = Vector2(70, 72)`（`:9`）、
  按钮 `offset_right/bottom = 70/72`（`:18-19`）；控件自身 `anchors_preset = 0`、**没有任何 `position`**（`:7-12`）。
- 它的父节点是 **`uITopPropContainer`（HBoxContainer）**：
  `TowerDefenseBattleFeatureShovel.Init()` → `control.AddUIToTopPropContainer(shovelManager)`
  （`TowerDefenseBattleFeatureShovel.cs:25`）→ `TowerDefenseControlNew.AddUIToTopPropContainer`
  （`TowerDefenseControlNew.cs:1127-1130`）→ `uITopPropContainer.AddChild(...)`（字段：`TowerDefenseControlNew.cs:31`）。
- 场景层级：`TowerDefenseControlNew` → `BankUILayer`(CanvasLayer, `TowerDefenseControlNew.tscn:200`) →
  `UITopContainer`(HFlowContainer, `:203`) → `UITopPropContainer`(HBoxContainer, `:226`)。
- **还有三个系统会覆盖位置**（所以直接改 `Position` 一定会被每帧刷回去）：
  1. `TowerDefenseCoexistHud.Arrange`：`uITopPropContainer.TopLevel = true` +
     `Position = (screen.X − 112 − 容器最小宽, 0)`（`TowerDefenseCoexistHud.cs:335-339`）；
  2. `TowerDefenseCoexistHud.ArrangeTools`：**每帧**遍历 `uITopPropContainer` 的子节点，对 `ShovelManager`
     强制 `TopLevel = mobile`；手机预设下还直接 `Position = screen − (宽+12, 高+12)`（`TowerDefenseCoexistHud.cs:199-223`）；
  3. `UITopContainer` 的 `Enter` / `Exit` 动画会改 `position` 与 `visible`
     （`position` 在 `(0,0)` 与 `(0,−88)` 之间过渡，`TowerDefenseControlNew.tscn:41-83`）。

⇒ 结论：**必须先把控件搬出 `HBoxContainer`**（reparent 到自建全屏 `Control`），再自己按百分比定位；
否则容器排版 / CoexistHud 每帧都会覆盖。这与「手套」Mod 的 `GloveManager` 处境完全一致。

### 3. 为什么 reparent 之后必须"修残局"（本 Mod 最关键的坑）

`ShovelManager._ExitTree`（`ShovelManager.cs:137-160`）比 `GloveManager._ExitTree`
（`GloveManager.cs:46-58`）狠得多：除了摘按钮回调、释放地图影子，还把**四个运行时命脉直接置 null**：

| `_ExitTree` 干了什么 | 不补的后果 | 本 Mod 的补法 |
|---|---|---|
| `shovelButton.Pressed -= ShovelButtonPressed`（`:146-149`） | 按钮**点不动** | 先 `-=` 再 `+=` `ShovelManager.ShovelButtonPressed`（public） |
| `mapShovelSprite.QueueFree(); mapShovelSprite = null`（`:151-155`） | 拖着铲子时**没有跟随鼠标的图标**（`ProcessShovelPick`（`:280-291`）与 `ShovelPickTool.GetMapSprite`（`ShovelPickTool.cs:59-66`）都靠它） | 按 `_Ready` 原逻辑新建 `Sprite2D`（贴图取 `shovelConfig.texture`、`Scale = 80/贴图宽`、`Visible=false`）并回填公开字段 |
| `mapControl = null; mapFeature = null; shovelConfig = null`（`:156-158`） | `ProcessShovelPick` 里 `mapControl.*` / `mapFeature.GetGroundHeight` 空引用 ⇒ 铲子彻底坏 | `ResolveShovel()`（public，`:57-69`，幂等）+ `Init(mapControl, mapFeature)`（public，`:237-245`，顺带把影子挂回 `mapControl.spriteNode`，与 `_Ready` 的挂点 `:92-95` 一致） |
| `_globalFeatureManager = null`（`:143`） | `CanUseShovel()`（`:113-122`）**恒 false** ⇒ 按钮点了没反应、快捷键也失效 | 反射回填 `_globalFeatureManager = GlobalFeatureManager.Instance`，并用 `Delegate.CreateDelegate` 把宿主私有 `OnGlobalFeatureChanged` 重新挂回 `GlobalFeatureManager.FeatureChanged`（先摘再挂，绝不重复） |

另外 `_ExitTree` 里的 `ShovelReset()` 会**取消当前选中**，所以本 Mod 在「玩家正拿着铲子（`shovelPick == true`）」
时**推迟**应用新位置，等放下后再搬。

以上补齐全部发生在 `MoveChild()` 里 `RemoveChild` + `AddChild` 之后的**同一帧**，中间不留任何空窗。

### 4. 视口 / 横竖屏 / 窗口缩放

- 百分比按**运行时视口**算：`pos = (视口 − 控件尺寸) × 百分比 / 100`，
  所以拉满 100% 也不会有一半跑出屏幕（上限同样按控件尺寸扣减，见 `PosEditorRow.Ensure` 的 `maxX/maxY`）。
- 每帧都会重新读 `GetViewport().GetVisibleRect().Size` ⇒ **窗口拉伸 / 手机旋转后自动按新视口重算**，不需要重进关卡。
- 顶部栏被动画收起时（`UITopContainer.visible=false` / 滑到 `y=−88`），本 Mod 按
  `nativeParent.IsVisibleInTree()` 同步 `ShovelManager.Visible` ⇒ 与原生表现一致（不会出现"顶部栏收起来了铲子还挂在屏幕上"）。

## 配置项（全部 immediate / affectsGameplay=false）

| key | 类型 | 默认 | 说明 |
|---|---|---|---|
| `enabled` | bool | `true` | 总开关。关 = 完全不动，铲子保持原生位置（战斗中关掉会立刻搬回原位）。 |
| `landMode` | enum | `auto` | `auto` / `tl tc tr ml mc mr bl bc br` / `custom`。 |
| `landX` | int 0–100 | `80` | 按钮左边缘，按屏宽百分比（只在 `custom` 生效）。 |
| `landY` | int 0–100 | `2` | 按钮上边缘，按屏高百分比（只在 `custom` 生效）。 |
| `enableLog` | bool | `false` | 诊断日志（`[ShovelPos] …`）。 |
| `aboutChangelog` / `aboutAuthor` | text / richtext | — | 「关于」栏；`aboutAuthor` 首行必须与 mod.json 的 `name` 逐字一致（收款码归属判定）。 |

## 构建 / 打包

```bash
# 编译（多 Agent 并行时走这个，串行加锁）
python mods/_build_locked.py ShovelPosition

# 只编译（不打包不装机）
python mods/ShovelPosition/build_and_install.py

# 打包（编译产物 -> dist/ShovelPosition.pmod，带 4 道护栏）
python mods/ShovelPosition/build_pmod.py

# 包校验
python pvz-hybrid-mod-skills/tools/pmod-toolchain/verify_pmod.py mods/ShovelPosition/dist/ShovelPosition.pmod

# 编译 + 打包 + 装机（⚠️ 装机由 Lead 统一做，本 Mod 不自带 --install）
python mods/_build_locked.py ShovelPosition --install
```

## 共享组件（只读引用，一个字节都不许改）

`..\..\PosEditorRow.cs`、`..\..\BattleOptionColumns.cs`、`..\..\QrCodeBitmap.cs`、`..\..\SponsorQrInjector.cs`。

## 已知副作用（与「手套」Mod 同一类，均来自"把控件搬出托管容器"）

- 铲子离开 `uITopPropContainer` 后，该容器的 `GetCombinedMinimumSize()` 少 70px：
  - 普通布局（`ArrangeLegacy`）：银行 / 卡片宽度由 `_views` 计算，**不受影响**；
  - 共存布局（`TowerDefenseCoexistHud.Arrange`）：`x2 = uITopPropContainer.GetCombinedMinimumSize().X`
    会参与算可用宽度（`TowerDefenseCoexistHud.cs:302-305`）⇒ 银行可用宽度 +70px、
    容器右对齐后里面的其它工具按钮右移 70px。与「手套」Mod 已在线上跑的是同一行为，不视为缺陷。
- `auto`（默认）与「启用位置调整」= 关时**完全不 reparent**，上述副作用不存在。

## 仍待实机验证

1. 普通关卡里铲子按钮是否正常出现、点一下是否进入铲子模式、再点植物是否正常铲掉。
2. 「位置」选九宫格 / 自定义后按钮是否落在预期位置；**窗口拉伸 / 手机旋转后**是否跟随重算；
   横竖屏各看一次。
3. 自定义位置下**铲子快捷键**（`Shovel` 动作）是否仍能切换选中；按钮按下态与地图铲子图标是否一致
   （没补好 `mapShovelSprite` 的话拖动时不会有图标 —— 这是本次最需要盯的回归点）。
4. 「自动（原位置）」切回去后：按钮是否**回到顶部栏原位置**（与没装本 Mod 时逐像素一致）。
5. 面板里第二行「启用 · 位置调整」关掉后，按钮是否立刻回原位；再打开是否立刻按设置摆放。
6. 手机预设（`MobilePreset`）下：`TowerDefenseCoexistHud.ArrangeTools` 会每帧给容器内子节点写位置，
   本 Mod 已把控件搬出容器 ⇒ 预期不再被覆盖（这是本 Mod 相对"直接改 position"方案的关键优势）。
7. 落盘可自证：每次在面板里改「位置」或开关，`godot.log` 都应出现
   `[ShovelPos] 配置已落盘：landMode=…, landX=…, landY=…`；重启游戏后面板里的取值应保持。
   若出现 `[ShovelPos] 配置保存失败：…`，把该行发给作者即可定位（宿主保存管道拒绝，消息带原因）。
8. 与「手套」Mod 同时安装：两个 Mod 各自一个面板（`ModPosLayer_shovelpos` / `ModPosLayer_glove`）、
   各自一个入口按钮（`ModPosBtn_shovelpos` / `ModPosBtn_glove`），打开一个会自动收起另一个；
   两边都设成自定义位置后不应互相覆盖。
