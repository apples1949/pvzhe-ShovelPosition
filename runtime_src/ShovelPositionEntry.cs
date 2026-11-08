using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「铲子 · 位置」Mod —— 托管运行时入口。
///
/// ── 需求 ────────────────────────────────────────────────────────────────
/// 把战斗界面上的**铲子按钮**做成"位置可调 + 可持久化 + 即时生效"，
/// 交互与持久化**照抄「手套」Mod（Glove v1.0.6）**：
///   1. 配置项 `landMode`（auto / 九宫格 tl…br / custom）+ `landX` / `landY`（int 百分比）；
///   2. 战斗设置页 Mod 按钮列（`ModCol4`）注入「铲子·设置」→ 打开**本 Mod 独占**浮层面板；
///   3. 面板第一行「位置」复用共享组件 `PosEditorRow.PanelSpec` / `PosEditorRow.Ensure`；
///   4. 改动即时生效（下一帧定位就用新值）+ 落盘（`[ShovelPos] 配置已落盘：…`）。
///
/// ── 游戏侧依据（全部实查 unpack/ = 0.32.0.0，给行号）──────────────────
/// · 铲子 Feature 注册名 `"Shovel"`：`unpack/Registry/Battle/TowerDefenseBattleRegistry.cs:33`
///   依赖 `"Map"`、`"PacketPick"`（同文件 :72）。
/// · 铲子按钮的**宿主类**是 `ShovelManager : Control`
///   （`unpack/Registry/Battle/Feature/Shovel/ShovelManager/ShovelManager.cs:6`），
///   它自己就是那个 70×72 的「铲子底座 + 铲子图标」控件：
///   `ShovelButton`（TextureButton，贴图 ShovelBank.png）+ 子节点 `ShovelSprite`（Sprite2D）。
/// · **prefab 里没有写死屏幕坐标**：`ShovelManager.tscn` 只给了
///   `custom_minimum_size = Vector2(70, 72)`（:9）与按钮的 `offset_right/bottom = 70/72`（:18-19），
///   控件本身 `anchors_preset = 0`、**没有任何 position**（:7-12）⇒ 位置完全由父容器排版算出。
/// · **父容器 = `uITopPropContainer`（HBoxContainer）**：
///   `TowerDefenseBattleFeatureShovel.Init()` → `control.AddUIToTopPropContainer(shovelManager)`
///   （`TowerDefenseBattleFeatureShovel.cs:25`）→
///   `TowerDefenseControlNew.AddUIToTopPropContainer`（`TowerDefenseControlNew.cs:1127-1130`）
///   → `uITopPropContainer.AddChild(...)`；字段声明见 `TowerDefenseControlNew.cs:31`。
///   场景层级：`TowerDefenseControlNew` → `BankUILayer`(CanvasLayer, :200) →
///   `UITopContainer`(HFlowContainer, :203) → `UITopPropContainer`(HBoxContainer, :226)。
/// · 从 `TowerDefenseBattleFeatureShovel.shovelManager`
///   （`TowerDefenseBattleFeatureShovel.cs:10`，public）或
///   `TowerDefenseBattleFeatureMap.shovelManager`
///   （`unpack/Registry/Battle/Feature/Map/TowerDefenseBattleFeatureMap.cs:100`）取到控件。
/// · 位置是**运行时算的**，而且会被别的系统覆盖：
///   ① `TowerDefenseCoexistHud.Arrange` 把 `uITopPropContainer.TopLevel = true` 并
///      `Position = (screen.X − 112 − 容器最小宽, 0)`（`TowerDefenseCoexistHud.cs:335-339`）；
///   ② `TowerDefenseCoexistHud.ArrangeTools` **每帧**遍历 `uITopPropContainer` 的子节点，
///      对 `ShovelManager` 强制 `TopLevel = mobile`，手机预设下还直接写
///      `Position = screen − (宽+12, 高+12)`（`TowerDefenseCoexistHud.cs:199-223`）；
///   ③ `UITopContainer` 有 `Enter/Exit` 动画改 `position` 与 `visible`
///      （`TowerDefenseControlNew.tscn:47-83`）。
///   ⇒ 直接改 `Position` 会被容器/CoexistHud 每帧覆盖，**必须先把控件搬出 HBoxContainer**，
///     这正是「手套」Mod 的做法（README「为什么要 reparent」一节）。
///
/// ── 关键坑：搬动会触发 `ShovelManager._ExitTree`（`ShovelManager.cs:137-160`）──────
/// 它比 `GloveManager._ExitTree`（`GloveManager.cs:46-58`）狠得多，除了摘按钮回调 / 释放影子，
/// 还把三个**运行时命脉字段直接置 null**：
///   `shovelButton.Pressed -= ShovelButtonPressed`（:146-149）、
///   `mapShovelSprite.QueueFree(); mapShovelSprite = null`（:151-155）、
///   `mapControl = null; mapFeature = null; shovelConfig = null`（:156-158）、
///   `_globalFeatureManager = null`（:143）。
/// 而**重新进树不会再跑 `_Ready`** ⇒ 不补齐的话：按钮点不动（回调没了）、
///   `CanUseShovel()` 恒 false（featureManager 没了 ⇒ 铲子直接失效）、
///   拖动时没有跟随鼠标的铲子图标（mapShovelSprite 没了）、
///   `ProcessShovelPick` 里 `mapControl.xxx`/`mapFeature.GetGroundHeight` 全部空引用。
/// 所以本 Mod 在**搬完的当帧**就按宿主 `_Ready` / `Init` 的原逻辑逐项补齐（见 `RepairAfterMove`）。
///
/// ── 铁律 ────────────────────────────────────────────────────────────────
/// · 入口三回调一律 try/catch（抛出 = 整包回滚，玩家看到"加载失败"）。
/// · 本程序集**没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` 不会被调用，
///   每帧逻辑一律挂 `SceneTree.process_frame` 信号（照抄 Glove / SpawnRate / TimeStop）。
/// · 共享组件（`PosEditorRow` / `BattleOptionColumns` / `SponsorQrInjector` / `QrCodeBitmap`）
///   **只读引用，一个字节都不改**。
/// </summary>
public sealed partial class ShovelPositionEntry : IXWModRuntimeEntry
{
	// ================================================================ 常量

	/// <summary>必须与 mod.json 的 `name` **逐字一致** —— 收款码注入器靠"关于富文本是否以本名字开头"判定归属。</summary>
	private const string ModName = "铲子 · 位置";

	private const string LogPrefix = "[ShovelPos] ";

	// ── 配置键（与 mod.json 的 settings[].key 一一对应）──
	private const string KEnabled = "enabled";
	private const string KLandMode = "landMode";
	private const string KLandX = "landX";
	private const string KLandY = "landY";
	private const string KHideBeforeStart = "hideBeforeStart";
	private const string KEnableLog = "enableLog";

	/// <summary>铲子 Feature 的注册名（`TowerDefenseBattleRegistry.cs:33`）。</summary>
	private const string ShovelFeatureName = "Shovel";

	/// <summary>
	/// `StringName("Shovel")` 的缓存 —— `FindShovelManager` **每帧**都要查 Feature，
	/// 每次 new StringName 都会分配一个 Godot 字符串名（逐帧 GC 噪声），这里建一次复用。
	/// </summary>
	private static readonly StringName ShovelFeatureId = new StringName(ShovelFeatureName);

	/// <summary>自建全屏悬浮层（承载被 reparent 出来的铲子控件）的节点名。</summary>
	private const string OverlayName = "ModShovelPosOverlay";

	/// <summary>本 Mod 独占的面板（**不要**复用 glove / timestop 的 spec id）。</summary>
	private static readonly PosEditorRow.PanelSpec PosSpec =
		new PosEditorRow.PanelSpec("shovelpos", "铲子 · 设置", "铲子·设置");

	/// <summary>位置预设值（与 mod.json 里 landMode 的 options **逐字一致**）。</summary>
	private static readonly string[] PosPresetValues =
	{
		"auto", "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br", "custom"
	};

	private static readonly string[] PosPresetLabels =
	{
		"自动（原位置）", "左上", "中上", "右上", "左中", "正中", "右中", "左下", "中下", "右下", "自定义"
	};

	/// <summary>哪个预设代表"自定义百分比坐标"。</summary>
	private const string PosCustomValue = "custom";

	/// <summary>铲子控件尺寸（`ShovelManager.tscn:9` `custom_minimum_size = Vector2(70, 72)`）。</summary>
	private const float ShovelW = 70f;
	private const float ShovelH = 72f;

	/// <summary>扫描节流：每 N 帧做一次"注入设置页"这类较重逻辑（定位本身每帧都做）。</summary>
	private const int ScanStride = 6;

	/// <summary>战斗跑起来后再等几帧才动手（避免与宿主自己的 Init / _Ready 抢同一帧）。</summary>
	private const int RunGraceFrames = 6;
	/// <summary>等"选卡完成"信号的最长帧数（30 秒，纯兜底）。正常路径靠 `packetList` 或 `OnChooseOver` 判定。</summary>
	private const int StartWaitFrames = 60 * 30;

	// ================================================================ 运行状态

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _started;
	private bool _faultReported;
	private bool _optFaultReported;
	private bool _settingsSubscribed;
	private long _frame;

	/// <summary>诊断日志开关（配置项 enableLog；只走 `if (!开关) return;` 写法）。</summary>
	private bool _log;

	// ── 配置缓存（Settings.Changed 里刷新；每帧只读字段，不重复 Get）──
	private bool _enabled = true;
	private string _landMode = "auto";
	private int _landX = 80;
	private int _landY = 2;
	/// <summary>v1.0.1：「正式开战前不显示铲子」。选卡阶段宿主会把顶部栏整条拉起（可见），
	/// 我们把控件搬出来后它就不跟着宿主收起了 ⇒ 选卡界面右下角会冒出一把铲子。
	/// 开（默认）= 只在选卡完成后再显示；关 = 与 v1.0.0 行为一致。</summary>
	private bool _hideBeforeStart = true;

	// ── 开战判定（v1.0.1）──
	/// <summary>本局是否已经"选卡完成/正式开战"。</summary>
	private bool _battleStarted;
	/// <summary>已挂上 `OnChooseOver` 的 PacketBank Feature（换关要摘）。</summary>
	private object _hookedFeature;
	/// <summary>等待开战信号的帧数（兜底用）。</summary>
	private int _startWaitFrames;

	// ── 战斗状态 ──
	/// <summary>当前关卡的 key（`currentLevelConfig.name`），用于识别换关后复位一次性标志。</summary>
	private string _levelKey;
	/// <summary>`isGameRunning` 连续帧数（用于 RunGraceFrames 宽限）。</summary>
	private int _runFrames;
	/// <summary>自建悬浮层（被 reparent 的铲子控件挂在这上面）。</summary>
	private Control _overlay;
	/// <summary>`enabled=false` 时"搬回原位"只做一次（每次换关重置）。</summary>
	private bool _disableRestored;
	/// <summary>
	/// 铲子控件**第一次被我们看到时的父节点**（= 游戏给它的「原位置」）。
	/// 不硬编码 `uITopPropContainer`：宿主在少数布局下会把工具挂到别处，记现场更稳。
	/// </summary>
	private Node _nativeParent;
	/// <summary>上一次定位模式（用于只在"模式变化"时打日志，不刷屏）。</summary>
	private string _appliedMode;
	/// <summary>补齐副作用失败的告警只报一次（每次换关重置）。</summary>
	private bool _repairWarned;
	/// <summary>"已重建地图铲子影子"提示只报一次（每次换关重置）。</summary>
	private bool _mapSpriteLogged;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			ReloadSettings();
			Info("初始化完成；PackageRoot=" + ((context == null) ? "<null>" : context.PackageRoot)
				+ "；enabled=" + _enabled + "，位置=" + _landMode + "(" + _landX + "," + _landY + ")。");
		}
		catch (Exception ex)
		{
			SafeWarn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				SafeWarn("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tickCallable);
			_started = true;
			try
			{
				if (_context?.Settings != null && !_settingsSubscribed)
				{
					_context.Settings.Changed += OnSettingsChanged;
					_settingsSubscribed = true;
				}
			}
			catch { }
			// ★ 不受 enableLog 门控：每次启动只打 1 行，用来确认"包真的加载且入口跑起来了"。
			try
			{
				GD.Print(LogPrefix + "v1.0.0 ready（铲子位置；enableLog=" + (_log ? "on" : "off") + "）");
			}
			catch { }
			Info("已挂载 process_frame（每 " + ScanStride + " 帧轮询一次「设置页入口」）。");
		}
		catch (Exception ex)
		{
			SafeWarn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			try
			{
				if (_context?.Settings != null && _settingsSubscribed) { _context.Settings.Changed -= OnSettingsChanged; }
			}
			catch { }
			_settingsSubscribed = false;
			// 卸载前尽量把控件搬回游戏原来的容器（搬不回去也无所谓，下面的 QueueFree 会连带回收）
			try { RestoreNativeParent(); } catch { }
			try
			{
				if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) { _overlay.QueueFree(); }
			}
			catch { }
			_overlay = null;
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
			}
		}
		catch (Exception ex)
		{
			SafeWarn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 配置读取

	private void ReloadSettings()
	{
		try
		{
			_enabled = ReadBool(KEnabled, true);
			_landMode = NormalizeMode(ReadString(KLandMode, "auto"));
			_landX = ReadInt(KLandX, 80, 0, 100);
			_landY = ReadInt(KLandY, 2, 0, 100);
			_hideBeforeStart = ReadBool(KHideBeforeStart, true);
			_log = ReadBool(KEnableLog, false);
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try
		{
			ReloadSettings();
			if (_enabled) { _disableRestored = false; }
			Info("配置已刷新：启用位置调整=" + (_enabled ? "开" : "关")
				+ "，位置=" + _landMode + "(" + _landX + "%," + _landY + "%)。");
		}
		catch { }
	}

	/// <summary>读 bool（兼容 bool / "true" / JsonElement；取不到用默认值）。</summary>
	private bool ReadBool(string key, bool def)
	{
		try
		{
			if (_context?.Settings == null) { return def; }
			object v = _context.Settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return def;
	}

	/// <summary>读 int（宿主把 int 型配置规范化成 int；兼容 double / JsonElement）并钳到 [min,max]。</summary>
	private int ReadInt(string key, int def, int min, int max)
	{
		int v = def;
		try
		{
			if (_context?.Settings == null) { return def; }
			object raw = _context.Settings.Get<object>(key);
			if (raw is int i) { v = i; }
			else if (raw is long l) { v = (int)l; }
			else if (raw is double d) { v = (int)Math.Round(d); }
			else if (raw is float f) { v = (int)Math.Round(f); }
			else if (raw is string s && int.TryParse(s, out int si)) { v = si; }
			else if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
			{
				v = je.TryGetInt32(out int ji) ? ji : (int)Math.Round(je.GetDouble());
			}
		}
		catch { }
		return Mathf.Clamp(v, min, max);
	}

	/// <summary>读 string（兼容 string / JsonElement）。</summary>
	private string ReadString(string key, string def)
	{
		try
		{
			if (_context?.Settings == null) { return def; }
			object v = _context.Settings.Get<object>(key);
			if (v is string s) { return s; }
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				return je.GetString();
			}
		}
		catch { }
		return def;
	}

	/// <summary>写配置（宿主保存管道；失败只警告，不打断操作）。</summary>
	private void SaveSettings(Dictionary<string, object> changes)
	{
		try
		{
			if (_context?.Settings == null || changes == null || changes.Count == 0) { return; }
			if (!_context.Settings.TrySave(changes, out XWModSettingsSaveResult result))
			{
				SafeWarn("配置保存失败：" + ((result == null) ? "(无返回信息)" : result.Message)
					+ "；本次改动只在当前会话内生效，重启后会回到上一次成功保存的值。");
				return;
			}
			// ★ 成功也打一条**不受 enableLog 门控**的日志 —— 玩家反馈"改了设置重启就没了"时，
			//   先看 godot.log 里有没有这一行，就能分清"没落盘"还是"落盘了但读取/应用有问题"。
			try
			{
				System.Text.StringBuilder sb = new System.Text.StringBuilder();
				foreach (KeyValuePair<string, object> kv in changes)
				{
					if (sb.Length > 0) { sb.Append(", "); }
					sb.Append(kv.Key).Append('=').Append(kv.Value == null ? "<null>" : kv.Value.ToString());
				}
				GD.Print(LogPrefix + "配置已落盘：" + sb.ToString());
			}
			catch { }
			if (result != null && result.RestartRequired)
			{
				Info("配置已保存（宿主提示需重启生效）。");
			}
		}
		catch (Exception ex)
		{
			SafeWarn("配置保存异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>把任意外来字符串收敛成配置里声明的 11 个预设值之一。</summary>
	private static string NormalizeMode(string mode)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(mode)) { return "auto"; }
			string m = mode.Trim();
			for (int i = 0; i < PosPresetValues.Length; i++)
			{
				if (string.Equals(PosPresetValues[i], m, StringComparison.Ordinal)) { return m; }
			}
		}
		catch { }
		return "auto";
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		// ★ 配置页「关于」栏收款码（共享组件；内部 30 帧节流 + 插好后停止全树查找）。
		//   modName 必须与 mod.json 的 name 逐字一致 —— 注入器靠"该富文本是否以本 Mod 名开头"判定归属。
		try
		{
			SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, ModName,
				_context?.PackageRoot ?? "", "Resources/SponsorQR.png");
		}
		catch { }

		_frame++;
		bool poll = ((_frame % ScanStride) == 0);

		try
		{
			MaintainShovel(poll);
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				SafeWarn("铲子定位维护异常（本条只报一次）：" + ex.Message);
			}
		}

		if (!poll) { return; }
		try
		{
			EnsureOptionRows();
		}
		catch (Exception ex)
		{
			if (!_optFaultReported)
			{
				_optFaultReported = true;
				SafeWarn("战斗设置页注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 定位主流程

	/// <summary>
	/// 维持"铲子控件在正确的位置"。
	/// `poll == false` 的帧只做**定位**（便宜），设置页注入等重逻辑只在 poll 帧做。
	/// </summary>
	private void MaintainShovel(bool poll)
	{
		TowerDefenseManager mgr = null;
		try { mgr = TowerDefenseManager.Instance; } catch { }
		if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { ResetLevelState(); return; }

		TowerDefenseControlNew ctrl = null;
		try { ctrl = mgr.currentControl; } catch { }
		if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { ResetLevelState(); return; }

		// ── 换关 / 重开同一关：复位一次性标志（新关卡 = 新的 Feature 与控件）──
		string key = CurrentLevelKey(mgr);
		if (!string.Equals(key, _levelKey, StringComparison.Ordinal))
		{
			_levelKey = key;
			_runFrames = 0;
			_disableRestored = false;
			_repairWarned = false;
			_mapSpriteLogged = false;
			_nativeParent = null;
			_appliedMode = null;
			// ★ v1.0.1：换关 ⇒ 重新等一次"开战"信号
			_battleStarted = false;
			_startWaitFrames = 0;
			UnhookChooseOver();
			// 悬浮层**不清空**：旧战斗的节点会随场景一起销毁（`IsInstanceValid` 会失配），
			// 若控件存活（同一战斗内重开）则 `EnsureOverlay` 会复用它，不会凭空多建一层。
		}

		// 战斗是否真的跑起来了（关卡还没进 = 什么都不做）
		bool running = false;
		try { running = ctrl.isGameRunning; } catch { }
		if (!running) { _runFrames = 0; return; }
		_runFrames = Math.Min(_runFrames + 1, 100000);
		if (_runFrames < RunGraceFrames) { return; }

		ShovelManager sm = FindShovelManager(ctrl);
		if (sm == null) { return; }   // 本关没有铲子（关卡配置 Open=false / 未解锁该关）⇒ 什么都不做

		Node nativeParent = ResolveNativeParent(ctrl, sm);
		if (nativeParent == null) { return; }

		// ── 总开关关闭 ⇒ 搬回游戏原生容器（只做一次）──
		if (!_enabled)
		{
			if (poll && !_disableRestored)
			{
				_disableRestored = true;
				RestoreNative(sm, nativeParent, "总开关已关闭");
			}
			return;
		}

		ApplyPosition(sm, nativeParent, IsBattleStarted(ctrl));
	}

	// ================================================================ 开战判定（v1.0.1）

	/// <summary>
	/// 本局是否已经"选卡完成 / 正式开战"。
	/// 选卡阶段宿主会把顶部栏拉起来（`UITopContainer` 播放 Enter，`visible=true`），我们把
	/// `ShovelManager` 搬出容器后不再受它收起影响 ⇒ 选卡界面会出现一把悬空的铲子（玩家实测截图）。
	/// 判定顺序：
	///   ① 没有 PacketBank feature（挑战/IZM 等无选卡环节）⇒ 直接算开战；
	///   ② feature 声明 `skipPacketChoose` ⇒ 直接算开战；
	///   ③ 订阅 `OnChooseOver`（宿主"选卡完成"事件），触发即算开战；
	///   ④ 兜底：等满 `StartWaitFrames` 帧仍没信号（特殊模式），按开战处理，避免铲子永远不出现。
	/// </summary>
	private bool IsBattleStarted(TowerDefenseControlNew ctrl)
	{
		if (!_hideBeforeStart) { return true; }
		if (_battleStarted) { return true; }
		try
		{
			TowerDefenseBattleFeaturePacketBank pbf = null;
			try { pbf = TowerDefenseManager.Instance?.GetPacketBankFeature(); } catch { }
			if (pbf == null || !GodotObject.IsInstanceValid(pbf)) { _battleStarted = true; return true; }
			try { if (pbf.skipPacketChoose) { _battleStarted = true; return true; } } catch { }

			if (!ReferenceEquals(_hookedFeature, pbf))
			{
				UnhookChooseOver();
				pbf.OnChooseOver += OnChooseOverHook;
				_hookedFeature = pbf;
			}

			// 主判据：选卡完成前 `packetList` 是空的，确认后才出现选中的卡。
			try
			{
				if (pbf.packetList != null && pbf.packetList.Count > 0) { _battleStarted = true; return true; }
			}
			catch { }
		}
		catch { }

		_startWaitFrames++;
		if (_startWaitFrames > StartWaitFrames) { _battleStarted = true; return true; }
		return false;
	}

	private void OnChooseOverHook()
	{
		if (!_battleStarted)
		{
			_battleStarted = true;
			Info("检测到选卡完成（OnChooseOver）⇒ 开始显示铲子。");
		}
	}

	private void UnhookChooseOver()
	{
		try
		{
			if (_hookedFeature is TowerDefenseBattleFeaturePacketBank pbf && GodotObject.IsInstanceValid(pbf))
			{
				pbf.OnChooseOver -= OnChooseOverHook;
			}
		}
		catch { }
		_hookedFeature = null;
	}

	private void ResetLevelState()
	{
		_levelKey = null;
		_runFrames = 0;
		_disableRestored = false;
		_repairWarned = false;
		_mapSpriteLogged = false;
		_nativeParent = null;
		_appliedMode = null;
	}

	private static string CurrentLevelKey(TowerDefenseManager mgr)
	{
		try
		{
			TowerDefenseLevelBaseConfig cfg = mgr.currentLevelConfig;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg)) { return null; }
			return cfg.name;
		}
		catch { return null; }
	}

	/// <summary>
	/// 找本关的铲子控件（三条路，全部是宿主公开字段/API，不反射猜）。
	/// ① `currentControl.GetFeature("Shovel") as TowerDefenseBattleFeatureShovel` →
	///    `.shovelManager`（`TowerDefenseBattleFeatureShovel.cs:10`）—— 最权威；
	/// ② `TowerDefenseManager.GetMapFeature().shovelManager`
	///    （`TowerDefenseManager.cs:2034` + `TowerDefenseBattleFeatureMap.cs:100`）—— 兜底；
	/// ③ 顶部道具栏里按类型扫（`TowerDefenseControlNew.uITopPropContainer:31`）。
	/// </summary>
	private ShovelManager FindShovelManager(TowerDefenseControlNew ctrl)
	{
		try
		{
			TowerDefenseBattleFeature feature = ctrl.GetFeature(ShovelFeatureId);
			if (feature is TowerDefenseBattleFeatureShovel sf)
			{
				// Feature 在 ⇒ 它说了算：manager 无效 = 本关没开铲子，别再往下兜底
				return GodotObject.IsInstanceValid(sf.shovelManager) ? sf.shovelManager : null;
			}
		}
		catch { }

		try
		{
			TowerDefenseBattleFeatureMap mapFeature = TowerDefenseManager.GetMapFeature();
			if (GodotObject.IsInstanceValid(mapFeature) && GodotObject.IsInstanceValid(mapFeature.shovelManager))
			{
				return mapFeature.shovelManager;
			}
		}
		catch { }

		try
		{
			HBoxContainer top = null;
			try { top = ctrl.uITopPropContainer; } catch { }
			if (GodotObject.IsInstanceValid(top))
			{
				for (int i = 0; i < top.GetChildCount(); i++)
				{
					if (top.GetChild(i) is ShovelManager found && GodotObject.IsInstanceValid(found)) { return found; }
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>取"游戏给铲子的原位置容器"（第一次看到控件时记下；容器被销毁时退回 `uITopPropContainer`）。</summary>
	private Node ResolveNativeParent(TowerDefenseControlNew ctrl, ShovelManager sm)
	{
		try
		{
			if (_nativeParent == null || !GodotObject.IsInstanceValid(_nativeParent) || _nativeParent == _overlay)
			{
				Node p = sm.GetParent();
				if (p != null && GodotObject.IsInstanceValid(p) && p != _overlay) { _nativeParent = p; }
			}
		}
		catch { }
		if (_nativeParent == null || !GodotObject.IsInstanceValid(_nativeParent))
		{
			try { _nativeParent = ctrl.uITopPropContainer; } catch { _nativeParent = null; }
		}
		if (_nativeParent == _overlay) { _nativeParent = null; }
		return _nativeParent;
	}

	/// <summary>
	/// 按"预设 → 百分比"给铲子控件定位。
	/// · `auto`：**原样不动**（保持游戏原位置）；若之前被我们搬走过，搬回原容器。
	/// · 其余：把 `ShovelManager` reparent 到**自建全屏 Control**（脱离 HBoxContainer 托管布局），
	///   再按百分比（0–100）定位 —— 九宫格锚点等价于固定的 (0/50/100, 0/50/100)。
	/// </summary>
	private void ApplyPosition(ShovelManager sm, Node nativeParent, bool started)
	{
		string mode = NormalizeMode(_landMode);
		int px = Mathf.Clamp(_landX, 0, 100);
		int py = Mathf.Clamp(_landY, 0, 100);

		if (string.Equals(mode, "auto", StringComparison.Ordinal))
		{
			// 保持游戏原位置：先把我们搬出来的控件搬回游戏自己的容器（没搬过就什么都不做）
			RestoreNative(sm, nativeParent, "位置模式 auto");
			// ★ 自定义模式下我们会按"顶部栏是否可见"手动改 `Visible`；
			//   搬回原生容器后必须还原成 true，否则铲子可能一直隐形（容器不再替我们管这个字段）。
			try
			{
				if (sm.GetParent() == nativeParent && !sm.Visible) { sm.Visible = true; }
			}
			catch { }
			_appliedMode = mode;
			return;
		}

		// 玩家正拿着铲子时不动（reparent 会触发 _ExitTree → 取消选中）
		bool picking = false;
		try { picking = sm.shovelPick; } catch { }
		if (picking) { return; }

		Control host = EnsureOverlay();
		if (host == null) { return; }
		if (sm.GetParent() != host)
		{
			if (!MoveChild(sm, host)) { return; }
			Info("已把铲子控件 reparent 到自建全屏 Control（" + OverlayName
				+ "），不再受 HBoxContainer 托管布局约束。");
		}

		// 每帧轻量自愈：宿主的 mapShovelSprite 有可能被别处回收（_ExitTree / 换关残留）
		EnsureMapShovelSprite(sm);

		Vector2 vp = new Vector2(1333f, 600f);
		try
		{
			Vector2 r = host.GetViewport().GetVisibleRect().Size;
			if (r.X > 100f && r.Y > 100f) { vp = r; }
		}
		catch { }

		// 控件尺寸（被容器托管时是 70×72；拿不到就用场景里的最小尺寸）
		Vector2 sz = new Vector2(ShovelW, ShovelH);
		try
		{
			Vector2 cur = sm.Size;
			if (cur.X > 1f) { sz.X = cur.X; }
			if (cur.Y > 1f) { sz.Y = cur.Y; }
		}
		catch { }

		float fx, fy;
		PresetAnchor(mode, px, py, out fx, out fy);
		// 百分比按"可用空间"算（屏宽 − 控件宽），拉满 100% 也不会跑出屏幕
		float availX = Math.Max(0f, vp.X - sz.X);
		float availY = Math.Max(0f, vp.Y - sz.Y);
		Vector2 target = new Vector2(availX * fx / 100f, availY * fy / 100f);

		try
		{
			if (sm.Size.X < 1f || sm.Size.Y < 1f) { sm.Size = sz; }
			Vector2 cur = sm.Position;
			if (Math.Abs(cur.X - target.X) > 0.5f || Math.Abs(cur.Y - target.Y) > 0.5f)
			{
				sm.Position = target;
			}
			// 与原生表现一致：顶部栏（UITopContainer）被动画收起时，铲子也跟着不显示
			// （`IsVisibleInTree()` 在 CanvasItem 上，不在 Node 上）
			bool show = true;
			if (nativeParent != null && nativeParent is CanvasItem nativeItem
				&& GodotObject.IsInstanceValid(nativeItem))
			{
				show = nativeItem.IsVisibleInTree();
			}
			// ★ v1.0.1：**选卡/正式开战前一律不显示**。
			//   老逻辑只看"顶部栏是否可见"，而选卡界面里顶部栏是可见的（宿主播放 Enter），
			//   控件被我们搬出容器后不再跟着收起 ⇒ 选卡界面右下角会冒出一把铲子（玩家实测截图）。
			if (show) { show = started; }
			if (sm.Visible != show) { sm.Visible = show; }
		}
		catch { }

		if (!string.Equals(_appliedMode, mode, StringComparison.Ordinal))
		{
			_appliedMode = mode;
			Info("铲子位置 → " + mode + "(" + px + "%," + py + "%)，视口 " + (int)vp.X + "×" + (int)vp.Y + "。");
		}
	}

	/// <summary>预设 → 百分比锚点（custom 用滑块给的 X/Y）。</summary>
	private static void PresetAnchor(string mode, int customX, int customY, out float fx, out float fy)
	{
		fx = Mathf.Clamp(customX, 0, 100);
		fy = Mathf.Clamp(customY, 0, 100);
		switch (mode)
		{
			case "tl": fx = 0f; fy = 0f; break;
			case "tc": fx = 50f; fy = 0f; break;
			case "tr": fx = 100f; fy = 0f; break;
			case "ml": fx = 0f; fy = 50f; break;
			case "mc": fx = 50f; fy = 50f; break;
			case "mr": fx = 100f; fy = 50f; break;
			case "bl": fx = 0f; fy = 100f; break;
			case "bc": fx = 50f; fy = 100f; break;
			case "br": fx = 100f; fy = 100f; break;
			default: break;   // custom / 未知值：用滑块坐标
		}
	}

	/// <summary>
	/// 取/建**本 Mod 专属**的全屏悬浮层（`Control`，挂在战斗的 `bankUILayer` 下 ⇒
	/// 与银行/种子栏同一个 CanvasLayer，层级完全一致；`MouseFilter=Ignore` 不挡战场点击）。
	/// 换关后旧的层随旧战斗一起销毁 ⇒ 按"父节点是否还是当前 bankUILayer"判重建。
	/// </summary>
	private Control EnsureOverlay()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return null; }
			TowerDefenseControlNew ctrl = mgr.currentControl;
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return null; }

			Node want = null;
			try { want = ctrl.bankUILayer; } catch { }
			if (want == null || !GodotObject.IsInstanceValid(want)) { want = ctrl; }

			if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			{
				if (_overlay.GetParent() == want) { return _overlay; }
				try { _overlay.QueueFree(); } catch { }
				_overlay = null;
			}

			Control host = new Control();
			host.Name = OverlayName;
			host.MouseFilter = Control.MouseFilterEnum.Ignore;   // 空白处不挡战场点击
			host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			want.AddChild(host, false, Node.InternalMode.Disabled);
			_overlay = host;
			Info("已建立铲子悬浮层（父节点=" + want.Name + "）。");
			return host;
		}
		catch (Exception ex)
		{
			SafeWarn("创建铲子悬浮层异常（已吞）：" + ex.Message);
			return null;
		}
	}

	/// <summary>
	/// 把控件从当前父节点搬到新父节点（**手工 RemoveChild + AddChild**，行为确定）。
	/// ⚠️ 这一步会触发 `ShovelManager._ExitTree`（摘按钮回调、释放地图铲子影子、**把
	/// shovelConfig / mapControl / mapFeature / _globalFeatureManager 置 null**），
	/// 所以搬完必须立刻 `RepairAfterMove` 逐项补齐。
	/// </summary>
	private bool MoveChild(Node child, Node newParent)
	{
		try
		{
			if (child == null || newParent == null) { return false; }
			if (!GodotObject.IsInstanceValid(child) || !GodotObject.IsInstanceValid(newParent)) { return false; }
			if (child.GetParent() == newParent) { return true; }
			Node old = child.GetParent();
			if (old != null && GodotObject.IsInstanceValid(old)) { old.RemoveChild(child); }
			newParent.AddChild(child, false, Node.InternalMode.Disabled);
			// ★ 搬完的**同一帧**补齐 _ExitTree 造成的损失（不留任何空窗）
			if (child is ShovelManager sm) { RepairAfterMove(sm, newParent.Name.ToString()); }
			return true;
		}
		catch (Exception ex)
		{
			SafeWarn("搬动铲子控件异常（已吞）：" + ex.Message);
			return false;
		}
	}

	/// <summary>搬回游戏原生容器（幂等：已经在原容器就什么都不做）。</summary>
	private void RestoreNative(ShovelManager sm, Node nativeParent, string why)
	{
		try
		{
			if (sm == null || !GodotObject.IsInstanceValid(sm)) { return; }
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent)) { return; }
			if (sm.GetParent() == nativeParent) { return; }
			if (MoveChild(sm, nativeParent))
			{
				Info(why + " ⇒ 铲子已搬回游戏原始容器（恢复原生位置）。");
			}
		}
		catch { }
	}

	/// <summary>卸载时把控件搬回游戏原容器（搬不动就算了）。</summary>
	private void RestoreNativeParent()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return; }
			TowerDefenseControlNew ctrl = mgr.currentControl;
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return; }
			ShovelManager sm = FindShovelManager(ctrl);
			if (sm == null) { return; }
			Node nativeParent = _nativeParent;
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent) || nativeParent == _overlay)
			{
				nativeParent = ctrl.uITopPropContainer;
			}
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent)) { return; }
			RestoreNative(sm, nativeParent, "卸载");
		}
		catch { }
	}

	// ================================================================ 搬运后的副作用补齐（本 Mod 的核心修复）

	/// <summary>
	/// ★ 搬动 `ShovelManager` 后，按宿主 `_Ready` / `Init` 的原逻辑把 `_ExitTree`
	/// （`ShovelManager.cs:137-160`）清掉的东西**就地补齐**。五项缺一不可：
	///   ① `shovelButton.Pressed` 回调（:146-149 摘掉了）⇒ 不补则按钮"点不动"；
	///   ② `shovelConfig`（:158 置 null）⇒ 用公开 `ResolveShovel()` 复原（:57-69，幂等）；
	///   ③ `mapShovelSprite`（:151-155 QueueFree 掉了）⇒ 不补则拖动时**没有跟随鼠标的铲子图标**
	///      （`ProcessShovelPick` 就是拿它定位的，`ShovelManager.cs:280-291`；
	///       `ShovelPickTool.GetMapSprite` 也依赖它，`ShovelPickTool.cs:59-66`）；
	///   ④ `mapControl` / `mapFeature`（:156-157 置 null）⇒ 用公开 `Init(...)`（:237-245）复原，
	///      并把③重新挂到 `mapControl.spriteNode`（与 `_Ready` 同一挂点，:92-95）；
	///   ⑤ `_globalFeatureManager`（:143 置 null）⇒ **不补则 `CanUseShovel()` 恒 false**
	///      （:113-122）⇒ 铲子按钮点了也没反应、快捷键也失效。该字段是 private ⇒ 反射回填 +
	///      用 `Delegate.CreateDelegate` 把宿主自己的私有 `OnGlobalFeatureChanged` 重新挂回
	///      `GlobalFeatureManager.FeatureChanged`（先摘再挂，绝不重复）。
	/// </summary>
	private void RepairAfterMove(ShovelManager sm, string why)
	{
		if (sm == null || !GodotObject.IsInstanceValid(sm)) { return; }

		// ① 按钮回调（`_ExitTree` 里 `Pressed -= ShovelButtonPressed`）
		try
		{
			TextureButton btn = sm.shovelButton;
			if (btn != null && GodotObject.IsInstanceValid(btn))
			{
				try { btn.Pressed -= sm.ShovelButtonPressed; } catch { }
				try { btn.Pressed += sm.ShovelButtonPressed; } catch { }
			}
		}
		catch { }

		// ② 铲子配置（`_ExitTree` 置 null）
		try
		{
			if (!GodotObject.IsInstanceValid(sm.shovelConfig)) { sm.ResolveShovel(); }
		}
		catch { }

		// ③+④ 地图铲子图标 + mapControl / mapFeature
		try { EnsureMapShovelSprite(sm); } catch { }
		try
		{
			TowerDefenseBattleFeatureMap mapFeature = TowerDefenseManager.GetMapFeature();
			if (GodotObject.IsInstanceValid(mapFeature))
			{
				TowerDefenseMapControl mapControl = mapFeature.mapControl;
				if (GodotObject.IsInstanceValid(mapControl))
				{
					// Init 会写 mapControl / mapFeature，并在图标还没挂载时把它挂到 spriteNode
					sm.Init(mapControl, mapFeature);
				}
			}
		}
		catch { }

		// ⑤ 全局功能管理器（`CanUseShovel()` 的唯一解锁判据来源）
		RestoreGlobalFeatureManager(sm);

		// ⑥ 让宿主自己的可见性判定跑一次（按钮在/不在由 CanUseShovel 决定）
		try { sm.Start(); } catch { }

		Info("已补齐搬运副作用（" + why + "）：按钮回调 / 铲子配置 / 地图铲子图标 / mapControl / _globalFeatureManager。");
	}

	/// <summary>
	/// 重建 `mapShovelSprite`（`_Ready` 的原逻辑：new Sprite2D + 铲子贴图 + `Visible=false`），
	/// 并回填到宿主的公开字段上。挂载交给 `Init(...)`（父节点为空时才会挂，不会重复挂）。
	/// </summary>
	private void EnsureMapShovelSprite(ShovelManager sm)
	{
		try
		{
			if (sm == null || !GodotObject.IsInstanceValid(sm)) { return; }
			if (GodotObject.IsInstanceValid(sm.mapShovelSprite)) { return; }

			Sprite2D sp = new Sprite2D();
			sp.Name = "ModShovelMapSprite";
			sp.Visible = false;
			try
			{
				ShovelConfig cfg = sm.shovelConfig;
				if (GodotObject.IsInstanceValid(cfg) && GodotObject.IsInstanceValid(cfg.texture))
				{
					sp.Texture = cfg.texture;
					float w = cfg.texture.GetWidth();
					if (w > 0f) { sp.Scale = Vector2.One * (80f / w); }
				}
			}
			catch { }

			sm.mapShovelSprite = sp;

			// 若 mapControl 有效而 sprite 还没进树（Init 的挂载分支只跑一次），这里补挂一次
			try
			{
				TowerDefenseMapControl mapControl = sm.mapControl;
				if (sp.GetParent() == null && GodotObject.IsInstanceValid(mapControl)
					&& GodotObject.IsInstanceValid(mapControl.spriteNode))
				{
					mapControl.spriteNode.AddChild(sp, false, Node.InternalMode.Disabled);
				}
			}
			catch { }

			if (!_mapSpriteLogged)
			{
				_mapSpriteLogged = true;
				Info("已重建地图铲子影子（mapShovelSprite）—— 拖动时才会重新有跟随鼠标的铲子图标。");
			}
		}
		catch (Exception ex)
		{
			SafeWarn("重建地图铲子影子异常（已吞）：" + ex.Message);
		}
	}

	// ── ⑤ 的反射缓存 ──
	private static System.Reflection.FieldInfo _gfmField;
	private static System.Reflection.MethodInfo _gfmHandlerMethod;
	private static bool _gfmProbed;

	/// <summary>
	/// 把 `GlobalFeatureManager.Instance` 回填到 `ShovelManager._globalFeatureManager`
	/// （private，反射），并把宿主自己的私有 `OnGlobalFeatureChanged(string,int,int)` 重新订阅到
	/// `GlobalFeatureManager.FeatureChanged` —— 这样"战斗中解锁铲子"时按钮显隐仍会即时刷新。
	/// 反射不到也不致命：字段回填失败时按钮会一直是旧显隐，日志会打一行明确告警。
	/// </summary>
	private void RestoreGlobalFeatureManager(ShovelManager sm)
	{
		try
		{
			GlobalFeatureManager gfm = GlobalFeatureManager.Instance;
			if (gfm == null || !GodotObject.IsInstanceValid(gfm)) { return; }
			const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance
				| System.Reflection.BindingFlags.NonPublic;
			if (!_gfmProbed)
			{
				_gfmProbed = true;
				try { _gfmField = typeof(ShovelManager).GetField("_globalFeatureManager", F); } catch { }
				try { _gfmHandlerMethod = typeof(ShovelManager).GetMethod("OnGlobalFeatureChanged", F); } catch { }
			}
			if (_gfmHandlerMethod != null)
			{
				try
				{
					Action<string, int, int> del = (Action<string, int, int>)Delegate.CreateDelegate(
						typeof(Action<string, int, int>), sm, _gfmHandlerMethod);
					try { gfm.FeatureChanged -= del; } catch { }
					try { gfm.FeatureChanged += del; } catch { }
				}
				catch { }
			}
			if (_gfmField != null)
			{
				_gfmField.SetValue(sm, gfm);
			}
			else if (!_repairWarned)
			{
				_repairWarned = true;
				SafeWarn("反射不到 ShovelManager._globalFeatureManager —— 位置已生效，但战斗中解锁/锁定铲子时按钮显隐可能不刷新（其它功能不受影响）。");
			}
		}
		catch { }
	}

	// ================================================================ 战斗设置页（PosEditorRow）

	/// <summary>扫战斗设置页对话框（`tree.Paused` 时才有），幂等注入「铲子·设置」按钮 + 位置行。</summary>
	private void EnsureOptionRows()
	{
		if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
		// 设置页 `pasue=true` ⇒ 平时用 `tree.Paused` 当廉价判据；
		// ⚠️ 多人模式下 `BattleOption._Ready` 会把 pasue 置 false 并解除暂停 ⇒ 只判 Paused 会永不注入。
		if (!_tree.Paused && !IsMultiplayerMode()) { return; }
		Node root = _tree.Root;
		if (root == null || !GodotObject.IsInstanceValid(root)) { return; }

		List<Node> opts = new List<Node>(4);
		CollectBattleOptionDialogs(root, opts, 4, 0);
		for (int i = 0; i < opts.Count; i++)
		{
			Node opt = opts[i];
			if (opt == null || !GodotObject.IsInstanceValid(opt)) { continue; }
			EnsurePosEditor(opt);
		}
	}

	/// <summary>递归收集名字以 `BattleOption` 开头的节点（对话框每次打开都是新实例）。</summary>
	private static void CollectBattleOptionDialogs(Node root, List<Node> outList, int max, int depth)
	{
		try
		{
			if (root == null || outList == null || outList.Count >= max || depth > 24) { return; }
			if (!GodotObject.IsInstanceValid(root)) { return; }
			int n = root.GetChildCount();
			for (int i = 0; i < n && outList.Count < max; i++)
			{
				Node ch = root.GetChild(i);
				if (ch == null || !GodotObject.IsInstanceValid(ch)) { continue; }
				string nm = ch.Name.ToString();
				if (nm.StartsWith("BattleOption", StringComparison.Ordinal))
				{
					outList.Add(ch);
					continue;
				}
				CollectBattleOptionDialogs(ch, outList, max, depth + 1);
			}
		}
		catch { }
	}

	private static bool IsMultiplayerMode()
	{
		try { return Global.IsMultiplayerMode; }
		catch { return false; }
	}

	/// <summary>
	/// 面板里一行「位置」（共享组件 PosEditorRow）+ 一行「启用位置调整」（本 Mod 自己往
	/// 面板的 Rows 容器里追加的原生 CheckBox）。改动即时生效 + 写配置。
	/// </summary>
	private void EnsurePosEditor(Node optionRoot)
	{
		try
		{
			float bw = ShovelW;
			float bh = ShovelH;
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr != null && GodotObject.IsInstanceValid(mgr))
				{
					TowerDefenseControlNew ctrl = mgr.currentControl;
					if (ctrl != null && GodotObject.IsInstanceValid(ctrl))
					{
						ShovelManager sm = FindShovelManager(ctrl);
						if (sm != null && GodotObject.IsInstanceValid(sm))
						{
							Vector2 s = sm.Size;
							if (s.X > 1f) { bw = s.X; }
							if (s.Y > 1f) { bh = s.Y; }
						}
					}
				}
			}
			catch { }

			// 行 id 用本 Mod 自己的名字（`ModPosRow_shovelpos_land`），避免与其他 Mod 撞车。
			PosEditorRow.Ensure(optionRoot, PosSpec, "shovelpos_land", "位置",
				PosPresetValues, PosPresetLabels, PosCustomValue,
				NormalizeMode(_landMode), _landX, _landY, bw, bh, ApplyLandFromEditor);

			// 第二行：总开关（同一个面板里再加一行，**不改共享组件一个字节**）
			EnsureEnabledRow();
		}
		catch (Exception ex)
		{
			SafeWarn("位置编辑器注入异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>「位置」那一行改动 ⇒ 写配置 + 当帧生效（下一帧 ApplyPosition 就会用新值定位）。</summary>
	private void ApplyLandFromEditor(string preset, int x, int y)
	{
		try
		{
			_landMode = NormalizeMode(preset);
			_landX = Mathf.Clamp(x, 0, 100);
			_landY = Mathf.Clamp(y, 0, 100);
			Dictionary<string, object> changes = new Dictionary<string, object>(3);
			changes[KLandMode] = _landMode;
			changes[KLandX] = _landX;
			changes[KLandY] = _landY;
			SaveSettings(changes);
			Info("铲子按钮位置 → " + _landMode + "(" + _landX + "%," + _landY + "%)。");
		}
		catch { }
	}

	// ================================================================ 「启用位置调整」面板行

	/// <summary>
	/// 在**本 Mod 自己的面板**里追加一行「启用位置调整」—— 一个 CheckBox 直接开关 `enabled`
	/// （改动即时写配置，下一帧就用新值；关掉时会立刻把铲子搬回原生位置）。
	///
	/// 为什么这么写（共享组件一个字节都没改）：`PosEditorRow.Ensure(...)` 已经把面板与行容器
	/// （`PanelSpec.RowsName`）建好了，这里只是往那个容器里再追加一行自己建的 Godot 原生控件；
	/// `SetPressedNoSignal` 回填 ⇒ 不会把"程序化回填"当成玩家操作（同共享组件踩过的坑）。
	/// </summary>
	private void EnsureEnabledRow()
	{
		try
		{
			VBoxContainer rows = FindPosRowsContainer();
			if (rows == null || !GodotObject.IsInstanceValid(rows)) { return; }

			const string rowName = "ModPosRow_shovelpos_enable";
			VBoxContainer row = rows.FindChild(rowName, true, false) as VBoxContainer;
			bool fresh = row == null || !GodotObject.IsInstanceValid(row);
			if (fresh)
			{
				row = new VBoxContainer();
				row.Name = rowName;
				row.AddThemeConstantOverride("separation", 2);
				row.ProcessMode = Node.ProcessModeEnum.Always;

				Label title = new Label();
				title.Text = "启用 · 位置调整";
				title.AddThemeFontSizeOverride("font_size", 13);
				title.AddThemeColorOverride("font_color", new Color(0.66f, 0.98f, 0.76f, 1f));
				title.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
				title.AddThemeConstantOverride("outline_size", 4);
				row.AddChild(title, false, Node.InternalMode.Disabled);

				CheckBox cb = new CheckBox();
				cb.Name = "Toggle";
				cb.Text = "启用铲子位置调整";
				cb.AddThemeFontSizeOverride("font_size", 14);
				cb.CustomMinimumSize = new Vector2(0f, 32f);
				cb.ProcessMode = Node.ProcessModeEnum.Always;
				cb.Toggled += (bool on) =>
				{
					try { ApplyEnabledFromEditor(on); }
					catch { }
				};
				row.AddChild(cb, false, Node.InternalMode.Disabled);

				Label hint = new Label();
				hint.Name = "Hint";
				hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
				hint.CustomMinimumSize = new Vector2(320f, 0f);
				hint.AddThemeFontSizeOverride("font_size", 10);
				hint.AddThemeColorOverride("font_color", new Color(0.78f, 0.86f, 0.95f, 1f));
				hint.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
				hint.AddThemeConstantOverride("outline_size", 3);
				hint.Text = "关掉 = 完全不动，铲子立刻回到游戏原生位置；"
					+ "开 = 按上面「位置」那一行摆放。与配置页里的「启用铲子位置调整」是同一个开关。";
				row.AddChild(hint, false, Node.InternalMode.Disabled);

				rows.AddChild(row, false, Node.InternalMode.Disabled);
			}

			CheckBox cbr = row.GetNodeOrNull<CheckBox>("Toggle");
			if (cbr != null && GodotObject.IsInstanceValid(cbr) && cbr.ButtonPressed != _enabled)
			{
				cbr.SetPressedNoSignal(_enabled);      // 不回触发 Toggled
			}
		}
		catch { }
	}

	/// <summary>按共享组件的命名约定（`PanelSpec.LayerName` / `RowsName`）找本 Mod 面板的行容器。</summary>
	private VBoxContainer FindPosRowsContainer()
	{
		try
		{
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null || !GodotObject.IsInstanceValid(root)) { return null; }
			string layerName = PosSpec.LayerName;
			string rowsName = PosSpec.RowsName;
			for (int i = 0; i < root.GetChildCount(); i++)
			{
				Node ch = root.GetChild(i);
				if (!(ch is CanvasLayer cl) || !GodotObject.IsInstanceValid(cl)) { continue; }
				if (!string.Equals(cl.Name.ToString(), layerName, StringComparison.Ordinal)) { continue; }
				VBoxContainer rows = cl.FindChild(rowsName, true, false) as VBoxContainer;
				if (rows != null && GodotObject.IsInstanceValid(rows)) { return rows; }
			}
		}
		catch { }
		return null;
	}

	/// <summary>面板里改了开关 ⇒ 立刻写配置（关掉时下一帧就把铲子搬回原位）。</summary>
	private void ApplyEnabledFromEditor(bool on)
	{
		try
		{
			_enabled = on;
			_disableRestored = false;
			Dictionary<string, object> changes = new Dictionary<string, object>(1);
			changes[KEnabled] = on;
			SaveSettings(changes);
			Info("启用位置调整 → " + (on ? "开" : "关") + "。");
		}
		catch { }
	}

	// ================================================================ 日志

	private void Info(string msg)
	{
		if (!_log) { return; }
		try { GD.Print(LogPrefix + msg); } catch { }
	}

	private void SafeWarn(string msg)
	{
		try { GD.PrintErr(LogPrefix + msg); } catch { }
	}
}
