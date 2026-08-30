# PoC-2: モデルバンドル経路（**企画のゲート**）

- 状態: **実施中（Chifuyu で変換は成功。ただし貫通は未解消。原因を特定し対処中）**
- 位置づけ: **企画のゲート**（要件定義書 D-1 / D-6）。これが通らなければ Yuni を作らない
- 関連: [ADR-0004](../adr/0004-unitypackage-first-coexist-with-moca.md)、[ADR-0003](../adr/0003-spcr-as-default-cloth-backend.md)、`../feasibility.md` 3.2 節

---

## 0. なぜこれがゲートなのか

Yuni の主目的は **unitypackage 配布モデルを、作者が設定したシェーダとクロスのまま動かすこと**である（A-3、[ADR-0004](../adr/0004-unitypackage-first-coexist-with-moca.md)）。VRM は moca に任せる。

したがって確かめるべきは「任意の VRM へクロスを自動構成できるか」（PoC-1）ではなく、**「手元の unitypackage モデルが、作者の作り込みを保ったまま座れるか」**である。

**この変更で最大の技術リスクが外れた。** モデルバンドル経路は Unity Editor 上でクロスを構成して焼き込むため、**SPCR のランタイム構築（R-12）を必要としない。** SPCR が本来想定しているエディタ操作の範囲で済む。

---

## 1. 何を確かめるのか

**Tokyo6 の配布モデルを、lilToon のマテリアルと作者のクロス設定を保ったまま AssetBundle 化し、Yuni 側で読んで座らせ、衣装が脚を貫通しないこと。**

### 判定基準

| # | 基準 |
|---|---|
| **合格** | Tokyo6 3 体（Chifuyu / Karin / Rikka）**すべて**で、座り動作中に衣装が脚を貫通しないこと。かつマテリアルが正しく描画されること |
| **要再判断** | 1 体でもモデル固有の手当てが必要になった場合。**その手当ての量を記録してから**判断する |
| **不合格** | 変換が原理的に成立しない、または手当ての量が「モデルごとに人手」の水準に達する場合 |

**「要再判断」を安易に「合格」へ寄せないこと。** モデルごとの手当てが際限なく必要になる構造は、moca が PMX で踏んで撤退した轍そのものである（moca ADR-0015 / R-12）。

### 副次的に測ること

- **v1 の詰め値のうち、どこまで SPCR へ移せるか**（移せない項目の一覧が F-18-5 のプレビュー要件になる）
- AssetBundle のファイルヘッダから `UnityFS` シグネチャとエンジンバージョンを読めるか（要件 1.3.1 節の前提。**崩れると自作コンテナの検討に戻る**）
- Unity バージョンをずらしたときに何が起きるか（読めないのか、静かに壊れるのか）
- 不正なバイト列を掴ませたときに落ちないか
- ビルドサイズと起動時間（要件 4.2 の目標値確定、U-7）

---

## 2. 環境

**[poc-1.md](poc-1.md) 2 節と同じプロジェクトを使う。** 構築済みの内容:

| 項目 | 状態 |
|---|---|
| Unity | ✅ `6000.0.82f1`（[ADR-0002](../adr/0002-unity-6000-0-lts.md)） |
| レンダーパイプライン | ✅ Built-in RP |
| SPCR JointDynamics | ✅ v2.0.11（`com.unity.burst` 1.8.30 と併せて導入済み） |
| UniVRM | ✅ |
| lilToon | ✅ 2.3.4 |
| Tokyo6 モデル | ✅ `C:\dev\yuni-assets\Tokyo6_*.unitypackage` 3 体 |

**追加で要るもの**: 座りモーション 1 本（[Mixamo](https://www.mixamo.com/) で `Sitting` → FBX for Unity → Rig を Humanoid に）。

> **`.gitignore` を確認すること。** `Assets/Tokyo6/` と `*.fbx` は除外済みだが、各ステップ後に `git status --short` を目視すること。**モデルの再配布は後から取り消せない**（NF-L-5）。

---

## 3. 手順

### Step 1 — 【関門 A】モデルが描画されるか

1. `Assets → Import Package → Custom Package` で `Tokyo6_Chifuyu.unitypackage` を取り込む
2. `Assets/Tokyo6/Characters/Chifuyu/Prefab/Chifuyu Variant.prefab` をシーンへ置く

| 確認 | 期待 | **実測（2026-08-31）** |
|---|---|---|
| マテリアル | lilToon で正しく描画される | ✅ **正常。** 3 種のシェーダ（`lts_o` / `lts_trans_o` / `lts_trans`）へ解決。取り込み時に 8 枚へ移行処理が走った（F-18-14 の根拠） |
| Missing Script | **14 件**（当初 7 件と見積もったが誤り） | ✅ Inspector には `None (Mono Script)` としか出ず、**値は一切見えない** |
| 揺れ物 | 一切動かない | ✅ 髪・スカートとも完全に静止 |
| Humanoid | Avatar が使えること | ✅ Chifuyu の FBX は `animationType: 3`（Humanoid）で配布されており、**そのままリターゲティングできた** |

**Missing Script 14 件の内訳:**

| 種別 | 件数 | 変換 |
|---|---|---|
| 構成情報（`Magica Bone Cloth_*` ×2、コライダ ×5） | 7 | **変換対象** |
| v1 が焼いた事前計算データ（`BoneClothData_` / `SelectionData_` / `BoneClothMeshData_`） | 7 | **捨てる。** SPCR には移せない |

### Step 2 — 【関門 B】貫通を再現させる

**変換を書く前に、問題が起きることを目で見ること。**

座りモーションを再生する。**クロスが完全に無い状態なので、確実に貫通する。** これが基準点になる。

> **実測（2026-08-31）— 貫通を確認。基準点を取得した。**
>
> Mixamo の座りモーション（FBX Binary / Without Skin / 60fps）を Humanoid でリターゲットして再生。
>
> - **持ち上げた太ももがスカートを完全に突き抜ける。** 脚を組む姿勢のため太ももが深く食い込み、**厳しめのテストケースになっている**
> - 髪は完全に静止（v1 の揺れ物が死んでいる証拠）
> - 座り姿勢そのものは正常（Humanoid リターゲティングが効いている証拠）
>
> **スカートのボーンは 14 本あるのに 1 本も動いていない。** 揺れ物として駆動されていないためである。変換後はこれらが SPCR に駆動され、太もものコライダに押しのけられるはずである。**その差が判定になる。**

### Step 3 — v1 の構成を読み出す

**MagicaCloth v1 を所持していなくても読める。** プレハブは YAML であり、Missing Script になっていても**シリアライズされた値は保持されている。**

Chifuyu で実測済みの内容（[ADR-0003](../adr/0003-spcr-as-default-cloth-backend.md)）:

```
clothTarget.rootList: 14 本         ← スカートのチェーンのルートボーン
teamData.colliderList: 2 個          ← 当てる相手（太もも 2 本だけ）
Capsule (LegD_L/R): length 0.137, startRadius 0.057, endRadius 0.058
Capsule (Chest):    length 0.068, radius 0.1
Sphere  (Head) ×2:  radius 0.07 / 0.057
```

**注目すべきは、スカートに割り当てられたコライダが太もも 2 本だけであること。** 胸も頭も衝突相手に入っていない。**当てる相手を増やすほど良いのではなく、絞り込みが正解**らしい。まずこの構成をそのまま再現すること。

**読み出したものは「クロス中間表現」へ落とすこと**（要件 F-17-13）。ソルバ固有の型をここへ持ち込まない。バックエンドを差し替えるときに効いてくる。

#### 探す場所は 2 箇所ある（2026-08-31 実測）

**`Magica_root` だけを見ても足りない。** 揺れ物の定義とコライダは別の場所にある。

```
Chifuyu Variant          ← FBX のプレハブインスタンス（stripped Transform 111 個）
├─ Body / Cloth / Face / Hair / NeckMesh
├─ Hips
│   └─ …ボーン階層…
│       ├─ Chest    → Magica Capsule Collider (Chest)     ★ここ
│       ├─ Head     → Magica Sphere Collider (Head) ×2     ★ここ
│       ├─ LegD_L   → Magica Capsule Collider (LegD_L)     ★ここ
│       └─ LegD_R   → Magica Capsule Collider (LegD_R)     ★ここ
└─ Magica_root
    ├─ Magica Bone Cloth_Skirt   ← 揺れ物の定義だけ
    └─ Magica Bone Cloth_Hair
```

| 探すもの | 場所 |
|---|---|
| 揺れ物のチェーン定義 | `Magica_root` 直下 |
| **コライダの形状・配置** | **ボーン階層の中に散在** |
| 両者の対応づけ | チェーン側の `teamData.colliderList`（**fileID 参照**） |

**コライダがボーン階層にあるのは正しい設計である。** 体に追従しなければ、脚を動かした瞬間に置いていかれる。**変換後も同じボーンの子として配置すること。**

`colliderList` は fileID による参照なので、**fileID からオブジェクトを解決する処理が要る。** 名前で引き当てないこと（同名オブジェクトが存在しうる。実際 `Magica Sphere Collider (Head)` は 2 個ある）。

#### Inspector からは読めない

Missing Script のコンポーネントは、Unity の Inspector では `None (Mono Script)` としか出ず、**シリアライズされた値は一切見えない。** 値は YAML には残っている。**だからファイルを直接読む。**

### Step 4 — SPCR へ変換して Unity Editor 上で構成する

**ここが PoC-1 との決定的な違いである。実行時構築は要らない。**

中間表現から SPCR の `SPCRJointDynamicsController` と `SPCRJointDynamicsCollider` を組む。**SPCR が想定しているエディタ操作の範囲**で済むため、README とサンプル（`Character.unity`）がそのまま参考になる。

| v1 | SPCR |
|---|---|
| `clothTarget.rootList` | ルートボーン群 |
| `Magica Capsule Collider` | `SPCRJointDynamicsCollider`（height > 0 でカプセル） |
| `Magica Sphere Collider` | `SPCRJointDynamicsCollider`（height = 0 で球） |
| `teamData.colliderList` | どのコライダに当てるかの指定 |
| 質量カーブ、`worldMoveInfluence` 等 | **移らない。** 見ながら合わせる |

**移せなかった項目を一覧で記録すること。** それが F-18-5（Packager のプレビュー）の要件になる。

#### SPCR の API 調査で判明したこと（2026-08-31）

**1. チェーン上の「全ボーン」に `SPCRJointDynamicsPoint` が要る。**

`SPCRJointDynamicsController.UpdateJointConnection()` は `SearchPoints()` で根から辿るが、**Point の無い GameObject に当たった時点で打ち切る。** 根だけに付けても繋がらない。根は `_IsFixed = true`（腰に固定）、以降は false。

**2. カプセルの向きの規約が v1 と違う。落とすと形が狂う。**

| | 軸の決め方 |
|---|---|
| MagicaCloth v1 | `axis` フィールド（0=X / 1=Y / 2=Z） |
| **SPCR** | **常に `transform.up`（Y）固定** |

Chifuyu の太ももコライダは `axis: 0`（X）かつ Transform に Z 軸まわり約 -90° の回転が入っている。**そのまま付けると 90° ずれる。** 補正回転を挟むこと。

```
R * (0,1,0) が v1 の軸方向に一致するようにする
  axis=0 (X) -> Quaternion.Euler(0, 0, -90)
  axis=1 (Y) -> identity
  axis=2 (Z) -> Quaternion.Euler(90, 0, 0)
```

**既存の v1 オブジェクトの回転を書き換えないこと。** 補正した新しい GameObject を作り、v1 側は触らない。元データを壊すと再実行できなくなる。

**3. 寸法の対応**

| v1 | SPCR |
|---|---|
| `startRadius` | `RadiusRaw` |
| `endRadius` | `RadiusTailScaleRaw` = endRadius / startRadius |
| `length` | `HeightRaw` |
| 球（`radius`） | `RadiusRaw` = radius、`HeightRaw` = 0（0 なら球扱い） |
| `center` | SPCR に相当物なし。**Transform の位置へ畳み込む** |

**4. 構築の呼び出し口**

`UpdateJointConnection()` → `UpdateJointDistance()` の順。エディタのインスペクタのボタンが呼んでいるものと同じであり、**実行時構築ではない**（PoC-1 との違い）。

### Step 5 — AssetBundle として焼く

`BuildPipeline.BuildAssetBundles` で prefab を焼く。**独自コンテナで包まないこと**（D-4）。

**注意点 2 つ:**

- **プレハブだけを焼くと壊れる**（F-18-13）。プレハブは FBX のインスタンスであり、ボーンの実体は FBX 側にある。依存を辿って FBX を含めること
- **lilToon の移行が完了してから焼くこと**（F-18-14）。取り込み時に 8 枚のマテリアルへ移行処理が走った。移行前に焼くと古い形式のまま固まる

### Step 6 — Yuni 側で読んで座らせる

1. **ロード前にファイルヘッダを読んで互換性を判定する**（F-18-8）
2. ロードして prefab を実体化する
3. 座りモーションを再生する
4. **衣装が脚を貫通しないことを目視で確認する**

> **実測（2026-08-31）— 変換は成功。ただし貫通は解消していない。**
>
> 変換の結果:
>
> | 項目 | 結果 |
> |---|---|
> | FBX Transform の解決 | 283 件 |
> | コライダ | 5 個すべて正しい親ボーンへ（`(LegD_L)` → `LegD_L` など名前が完全一致） |
> | スカート | ルート 14 本 / Point 84 個 / コライダ 2 個 |
>
> 見た目の変化:
>
> | | 変換前（基準点） | 変換後 |
> |---|---|---|
> | スカート | 硬い円錐のまま静止 | **太ももに押しのけられて変形する（駆動はしている）** |
> | 太もも | スカート面を突き抜けていた | **依然として突き抜けている** |
>
> **【重要な教訓】引きの絵で「解消した」と誤判定した。**
>
> 引きのアングルではスカートの縁から脚が出ているように見えたが、寄りで見ると太ももがスカート面を貫通していた。**貫通の判定を引きの絵で行ってはならない。** 1 節に「主観で『まあ動く』と言わない」と書いておきながら、それを破った。**寄りの絵で、脚が最も食い込む箇所を見ること。**
>
> **原因（特定済み）**: `SPCRJointDynamicsPoint._PointRadius` を 0 のままにしていた。
>
> SPCR の押し出し計算 `Collision.PushoutFromSphere(Center, Radius, pointRadius, ref point)` はこの値を使う。**0 だと布の厚みがゼロとして扱われ、メッシュがコライダを貫通する。**
>
> v1 は `clothParams.radius` として粒子半径 `0.017 → 0.035`（根から先端へ補間）を持っていたが、**変換で転記していなかった。** 抽出器の JSON には最初から入っていたのに、Unity 側で使っていなかった。
>
> **移せない設定と、移し忘れた設定は別物である。** 前者は記録して諦めるものだが、後者はただのバグである。F-18-5 のプレビュー要件は前者を提示するためのものであり、後者を見逃さない仕組みは別に要る。

> **実測（2026-08-31）— 3 体の構成を比較した。前提が崩れた。**
>
> Karin と Rikka のプレハブを unitypackage から直接取り出し、同じ抽出器にかけた。**Unity へインポートせずに構造を比較できた。**
>
> | モデル | チェーン | スカートのクロス |
> |---|---|---|
> | Chifuyu | Hair, **Skirt** | **あり** |
> | Karin | Hair_F, Hair_Side, Ribbon | **無し** |
> | Rikka | Hair (1), Hair | **無し** |
>
> **2/3 のモデルはスカートのクロスを持っていない。** 作者が設定しているのは髪とリボンだけである。
>
> しかも Karin と Rikka には**どのチェーンからも参照されていないコライダ**がある。
>
> | モデル | 未参照のコライダ |
> |---|---|
> | Chifuyu | なし |
> | Karin | LegD_L, LegD_R, Hips |
> | Rikka | LegD_L, LegD_R, Hips |
>
> 作者は太ももと腰のコライダを作ってあるが、**どの衣装にも当てていない。**
>
> **これは「モデル固有の手当てが要る」とは違う問題である。** 変換機構そのものは 3 体で一様に働く。壊れたのは前提のほうで、**「作者が作り込んでいるから、それを変換すれば貫通しない」が 3 体中 1 体でしか成り立たない。**
>
> 副次的な確認もできた。**Rikka の LegD コライダは `axis: 1`（Y）で、Chifuyu / Karin の `axis: 0`（X）と違う。** カプセルの向きの補正を軸ごとに実装しておいたのが効いた。決め打ちにしていれば Rikka で壊れていた。

### Step 7 — 残る 2 体で繰り返す

**Karin と Rikka でも同じ手順が通ること。** ここで初めて「モデルごとの手当てが要るか」が分かる。1 体目だけで判断しないこと。

---

## 4. 詰まりそうなところ

| # | 想定 | 対処 |
|---|---|---|
| 1 | マテリアルがピンク | lilToon が効いていない。シェーダの解決先を確認する |
| 2 | Missing Script の値が読めない | Unity の Inspector では見えないが、**プレハブを YAML として直接読めば取れる**。ADR-0003 の実測がその方法 |
| 3 | SPCR のコライダ指定が v1 と概念的に合わない | 中間表現の切り方を疑う。**当てる相手の絞り込み（太もも 2 本）が再現できているか**をまず確認する |
| 4 | AssetBundle でマテリアルが壊れる | シェーダがバンドルに含まれているか確認する |
| 5 | プレイヤー側で Missing Script になる | **F-18-4 のホワイトリストの話。** 剥がし忘れか、プレイヤーにアセンブリが無い |
| 6 | 特定のモデルだけ壊れる | **これが「要再判断」の入口である。** 手当てせずにまず記録する |

---

## 5. 記録すること

判定に必要なのは印象ではなく事実である。**次を書き残してから判定すること。**

- 3 体それぞれの結果（貫通する / しない / 条件付き）
- **v1 → SPCR で移せなかった設定の一覧**
- **モデル固有の手当てを要した箇所と、その内容**
- ヘッダからエンジンバージョンを読めたか
- Unity バージョンをずらしたときの挙動
- ビルドサイズと起動時間

---

## 6. 判定後にやること

**結果が出たら ADR を書く。通っても通らなくても書く。**

- **合格** → **ADR-0005** として記録し、[ADR-0001](../adr/0001-unity-for-cloth-and-emote.md) を確定させる。要件定義書の状態を「ドラフト」から進める。このブランチは破棄し、0.1 の実装を新しいブランチで始める
- **不合格** → **ADR-0005** として記録し、**[ADR-0001](../adr/0001-unity-for-cloth-and-emote.md) を廃止する。** 縮退先は ADR-0001 の「案 B」（Unity Editor 拡張によるオフライン変換ツール）。moca 本体は無改造で済む

**不合格は失敗ではない。** PoC の目的は、8 リリースぶんの労力を投じる前に答えを出すことである。ここで止まれたなら PoC は成功している。
