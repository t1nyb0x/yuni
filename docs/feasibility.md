# Yuni 実現可能性調査

- 版: 0.1（初版）
- 作成日: 2026-08-30
- 状態: ドラフト
- 前提: moca v0.7.0（2026-08-30 リリース）時点の仕様・決定を出発点とする

本書は「moca を Unity で作り直す」という判断の可否を記録する。要件そのものは `requirements.md` に置く。

---

## 1. 結論

**実現可能。ただし着手前に PoC-1 を通すことを条件とする。**

moca の [ADR-0017](https://github.com/t1nyb0x/moca/blob/main/docs/adr/0017-vrm-pmx-only.md) は代替案として挙げた「案 2: moca を Unity で再実装」を、非描画機能の全面再実装コストを理由に退けた。**Yuni はその案 2 そのものである。** 却下理由は消えていない。消えていないが、支払う価値があるかどうかが本調査の論点である。

支払う価値の判定は一点に集約される。**ランタイムで読み込んだ任意の VRM に対して、クロスシミュレーションを自動で適用できるか。** これができれば Yuni は moca が原理的に到達できない領域を得る。できなければ Yuni は「同じものを別の言語で書き直しただけ」になり、UI・LLM 通信・永続化・音声連携の再実装コストが丸損になる。

したがって **PoC-1 を企画のゲートとする。** 詳細は第 7 章。

---

## 2. 何が本当の問題か

「座ると服が貫通する」は、実際には性質の異なる 4 つの問題が重なっている。分けずに扱うと解決策を誤る。

| # | 問題 | moca での状況 | Unity 化で解けるか |
|---|---|---|---|
| P-1 | 大振りの動作そのものが作れない | F-14 はボーン回転による手続き的近似。「座る・手を振るといった動作は範囲外」と CHANGELOG に明記 | ✅ Humanoid Mecanim とモーションクリップで解ける |
| P-2 | ボーンのある衣装（スカート・コート）が脚を貫通する | VRM SpringBone は「垂れて押しのけられるだけ」（0.7 決定事項）。押し戻す力を持たない | ✅ クロスシミュレーション＋人体コライダで解ける |
| P-3 | ボーンの無い衣装が、脚の変形に引きずられて貫通する | 原理的に手が無い | ⚠️ 頂点駆動のクロス（MeshCloth 相当）が要る。**自動適用は自作が必要**（3.3 節） |
| P-4 | unitypackage 配布モデルをそのまま使えない | ADR-0017 で範囲外と決定 | ⚠️ 解けるが、**ランタイム読込ではなく AssetBundle 化が要る**（3.2 節） |

**P-1 だけであれば Unity 化は不要である。** three.js 側にもモーションクリップ再生の道はあり、moca が採らなかったのは「VRMA / VMD を同梱できず利用者負担になる」という配布上の理由であって技術的限界ではない。Unity 化を正当化するのは P-2 と P-3、とりわけ **P-3 は three-vrm 系のスタックが原理的に持たない能力**である点にある。

同時に、P-4 について誤解を潰しておく。**Unity アプリにしても `.unitypackage` はランタイムに読めない。** unitypackage は Unity Editor 向けのアーカイブ形式であり、その中のマテリアルや prefab はエディタでのインポート処理を経てはじめて意味を持つ。「Unity にすれば unitypackage が読める」は成り立たない。必要なのは第 3.2 節に述べる Packager である。

---

## 3. 技術的成立性

### 3.1 基盤（検証済み・低リスク）

| 要素 | 手段 | 判定 | 根拠 |
|---|---|---|---|
| VRM のランタイム読込 | [UniVRM](https://github.com/vrm-c/UniVRM) | ✅ | VRM 0.x / 1.0 の双方でランタイム import に対応。async/await API あり |
| 透過・枠なし・最前面 | [UniWindowController](https://github.com/kirurobo/UniWindowController)（MIT） | ✅ | 透過（Alpha / ColorKey）、クリックスルー（ヒットテスト Opacity / Raycast）、最前面、ドラッグ移動、ファイルドロップを提供。moca の F-13 がほぼそのまま乗る。Windows 10/11 対応 |
| Unity ライセンス | Unity 6 Personal | ✅ | Runtime Fee は 2024 年に撤回済み。Unity 6 より Personal でもスプラッシュスクリーンを非表示にできる |
| LLM の SSE ストリーミング | `System.Net.Http.HttpClient` | ✅ | `ResponseHeadersRead` + `StreamReader` で逐次読み。`UnityWebRequest` はストリーミングに向かないため使わない。メインスレッドへのマーシャリングは別途必要 |
| 資格情報の保管 | Windows 資格情報マネージャー | ✅ | `advapi32.dll` の `CredWrite` / `CredRead` を P/Invoke |
| ロジック層の単体テスト | Unity Test Framework（EditMode） | ✅ | `UnityEngine` に依存しない asmdef へロジックを隔離すれば NUnit でそのまま回る |

### 3.2 unitypackage 配布モデルへの対応（要 PoC-2）

**`.unitypackage` はランタイムに読めない。** 成立させる唯一の現実的な経路は AssetBundle である。

```
[利用者の Unity Editor]                            [Yuni ランタイム]

  unitypackage を import
  + 利用者自身の MagicaCloth2                .yuni
  + lilToon / MToon 等のシェーダ  ──焼く──▶  (AssetBundle) ──読む──▶ シーンへ配置
  + Yuni Packager（Editor 拡張）
```

これは Warudo が `.warudo` 形式で採っている構造と同じであり、前例がある。

**成立条件と制約:**

- AssetBundle に**コードは入らない。** Unity の `MonoScript` は「アセンブリ名 + 名前空間 + クラス名」の参照でしかない。したがって *Packager 側で参照したコンポーネントと同名のアセンブリが、Yuni プレイヤーのビルドに含まれていなければ Missing Script になる。* MagicaCloth2 をプレイヤーへ組み込むことは回避できない（第 4 章）
- Packager とプレイヤーで **Unity のバージョンを固定する必要がある。** Packager は Yuni 本体リポジトリに同梱し、CI でバージョンを縛る
- モデル作者独自の `Assembly-CSharp` 上のスクリプトは解決できない。Packager 側で**許可コンポーネントのホワイトリスト検証**を行い、未知の MonoBehaviour は剥がす。これは互換性対策であると同時に、信頼できない AssetBundle を読むことに対する防御でもある

### 3.3 クロスの自動適用（要 PoC-1 — 企画のゲート）

MagicaCloth2 は [Runtime Construction](https://magicasoft.jp/en/mc2_runtime_build/) API を持つ。エディタでの事前設定なしに、スクリプトからクロスを構築できる。

```csharp
var cloth = obj.AddComponent<MagicaCloth>();
var sdata = cloth.SerializeData;
sdata.clothType = ClothProcess.ClothType.BoneCloth;
sdata.rootBones.Add(transform);
cloth.BuildAndRun();
```

**これが Yuni の成立を支える一点である。** ただし BoneCloth と MeshCloth で確度が大きく違う。

| 経路 | 対象 | 自動化の見込み | 備考 |
|---|---|---|---|
| **BoneCloth** | ボーンのある衣装（P-2） | **高い。** VRM 1.0 / 0.x の SpringBone 定義は揺れ物のチェーンとコライダを標準化して持っている。これを読んで `rootBones` へ流し込むだけで構造が決まる | 人体コライダ（腰・尻・太もも・上腕）は Humanoid ボーンから手続き的にカプセルを生成する。**座り貫通の主因への直接の対処はこれ** |
| **MeshCloth** | ボーンの無い衣装（P-3） | **低い。要検証。** 公式が「頂点属性は省略できず、ペイントマップか属性配列で必ず指定する」と明記している。未知のユーザーモデルにペイントマップは用意できない | **属性配列をボーンウェイトから手続き生成する仕組みを自作する**必要がある（例: 腰・脚ボーンへの重みと、固定端からの測地距離で move / fixed を決める）。ここが唯一の技術的な賭け |

したがって **PoC-1 は BoneCloth 経路（P-2）で切る。** MeshCloth（P-3）は 1.0 以降の課題とし、P-3 が解けないことを前提に企画が成立するかを判断できる形にしておく。

### 3.4 非描画機能の移植（コストは大きいが不確実性は小さい）

moca は要件定義書 4.6 とアーキテクチャ文書で「依存の向きは常に内側（domain）へ向かう」「domain は three.js にも Tauri にも依存しない」と定めている。**この規律が移植を助ける。**

| moca の層 | Yuni での行き先 | 移植の性質 |
|---|---|---|
| `domain/emotion`（タグパーサ） | C# の純粋クラス | **機械的。** 仕様（`emotion-protocol.md`）とテストケースがそのまま使える |
| `domain/motion`（各コントローラ） | 同上 | **機械的。** `advance` / `evaluate` の純粋関数形式は C# にそのまま写る |
| `domain/lipsync`（カナ→ビセーム） | 同上 | **機械的** |
| Rust `llm/`（3 アダプタ + SSE） | C# の `IChatProvider` 実装 | **書き直し。** ただし外部インターフェース仕様（moca 要件 7.1〜7.4）が確定済みなので設計判断は不要 |
| Rust `storage/` `secret/` | C# | **書き直し。** 量は小さい |
| `render/`（three.js） | Unity のシーン・Animator | **作り直し。** 概念は移るがコードは移らない |
| `ui/`（React + Zustand） | UI Toolkit | **作り直し。最大のコスト。** 下記参照 |

**UI が最大の未知数である。** React での逐次表示・スクロール・設定画面・吹き出しを UI Toolkit で作り直す。UI Toolkit は USS / UXML で HTML / CSS に近い書き味を持つが、Markdown 整形表示は劣化する（TextMeshPro のリッチテキストは限定的で、まともな OSS の Markdown レンダラが無い）。moca の見た目をそのまま再現できるとは考えないこと。

---

## 4. ライセンス上の制約

### 4.1 MagicaCloth2 と Unity Asset Store EULA

「同梱」には 3 つあり、扱いが全部違う。混同すると判断を誤る。

| 何を | 可否 | 対処 |
|---|---|---|
| リポジトリへのコミット | **禁止** | `.gitignore` に入れる。**未所持でもリポジトリを clone してビルドが通ること**を設計要件にする（`IClothBackend` + Null 実装 + Scripting Define） |
| `.unitypackage` の再配布 | **禁止** | しない |
| 配布ビルドへの組み込み | **許可。かつ回避不可能** | EULA は "incorporated and embedded components" として明示的に許可している。禁じているのは*抽出可能な形*での再配布 |

3 つ目が要点である。3.2 節のとおり **AssetBundle にコードは入らない**ため、プレイヤー側に MagicaCloth2 のランタイムが無ければクロスは一切動かない。「配布ビルドにも入れない」を文字どおり実施すると Yuni を作る目的そのものが消える。

**抽出不能性の担保として IL2CPP ビルドを必須とする。** Mono ビルドは `<app>_Data/Managed/MagicaCloth2.dll` をそのまま配置するため、EULA の「抽出可能な形」に該当しうる。IL2CPP はネイティブコードへ変換するのでこの問題が構造的に起きない。

### 4.2 その他

- **モデルファイルは一切同梱・再配布しない。** moca 要件 4.4 をそのまま継承する
- シェーダ（lilToon、MToon 等）は AssetBundle に含めて配布することになる。採用するシェーダのライセンスを個別に確認すること
- Unity 6 Personal の収益条件（年間収益 20 万ドル未満）を超えないこと

---

## 5. 支払うことになるコスト

| 項目 | moca（現状） | Yuni（見込み） |
|---|---|---|
| 配布サイズ | 約 15MB（Tauri） | **150〜300MB**（Unity IL2CPP） |
| 起動時間 | 3 秒以内（要件値） | **5 秒**程度を見込む。3 秒は厳しい |
| 3D 非表示時の消費 | 「GPU / CPU をゼロ」（F-02-3） | **ゼロにはできない。** Unity のプレイヤーループは止まらない。`OnDemandRendering` と `targetFrameRate` で下げる形へ要件を緩和する |
| 必須ランタイム | WebView2 Runtime | **不要**（自己完結） |
| 貢献の敷居 | Node + Rust | **Unity Editor が必須**。加えてクロスを触るには MagicaCloth2 の購入が要る |
| 再実装量 | — | UI 全体、LLM 3 アダプタ、永続化、資格情報、音声連携、リップシンク駆動、マスコットウィンドウ |

**この表が ADR-0017 の「案 2 却下」の中身である。** Yuni はこれを承知のうえで支払う判断をする。

---

## 6. リスク

| # | リスク | 影響 | 軽減策 |
|---|---|---|---|
| R-1 | MeshCloth の頂点属性を任意モデルに対して自動生成できない | P-3（ボーン無し衣装の貫通）が解けない | **1.0 の条件から外す。** P-2 が解ければ Yuni の存在価値は成立すると位置づける。PoC-1 は P-2 で切る |
| R-2 | AssetBundle のバージョン整合が崩れる | 利用者が焼いた `.yuni` が読めない | Packager を本体リポジトリに同梱し Unity バージョンを CI で固定。bundle にスキーマ版を埋め、非互換時は明示的に断る |
| R-3 | AssetBundle は信頼できない入力である | 不正な bundle での異常終了、想定外のコンポーネント | 許可コンポーネントのホワイトリスト検証。読み込み失敗でアプリを落とさない（moca R-4 と同じ姿勢） |
| R-4 | Asset Store EULA 違反 | 権利上の問題 | `.gitignore` + IL2CPP 必須 + **MagicaCloth2 無しでビルドが通ることを CI で担保する** |
| R-5 | UI 再実装が moca の水準に届かない | 体験の後退。移行の動機が失われる | 0.1 の時点で moca と並べて比較する。Markdown 表示の劣化は既知の後退として受け入れ、記録する |
| R-6 | 起動時間・配布サイズの悪化 | マスコット用途としての体験劣化 | IL2CPP + Managed Stripping + テクスチャ圧縮。起動 5 秒を要件値とし、超えたら対策を打つ |
| R-7 | 透過ウィンドウ・クリックスルーが IL2CPP や高 DPI 環境で崩れる | F-13 相当が動かない | **PoC-3 で先に潰す。** UniWindowController は実績があるが moca の「外接矩形まで窓を詰める」要件は自作部分が残る |
| R-8 | moca と Yuni の二重管理が長引く | 開発リソースの分散 | moca は「後継が 1.0 に達した時点で保守終了」と明示的に宣言する。それまでは moca 側の新機能追加を止め、修正のみとする |
| R-9 | 座り姿勢でモデルの足が床にめり込む・浮く | 見た目が破綻する | マスコット表示には床が無い。エモートごとに接地基準（腰の高さ・足首の目標位置）を持たせ、身長で正規化する |
| R-10 | クロスが moca 対比の唯一の優位であるのに、リリース順では最後に来る | 途中で企画が座礁したとき何も残らない | **PoC をリリース計画の外に置き、0.1 の前に検証を終える。** 第 7 章 |

---

## 7. PoC 計画（リリース計画の外・着手前に実施）

**目的は「作れるか」の確認であって、作品を作ることではない。** 使い捨て前提で書き、本実装へは持ち込まない。

### PoC-1 — クロスの自動適用（**企画のゲート。これが通らなければ Yuni を作らない**）

1. UniVRM でユーザー VRM をランタイム読込する
2. 読み込んだモデルの VRM SpringBone 定義（チェーン・コライダ）を読み出す
3. それを MagicaCloth2 の BoneCloth へ変換し `BuildAndRun()` する
4. Humanoid ボーンから腰・尻・太もも・上腕にカプセルコライダを手続き生成する
5. **座りモーションを再生し、スカートが脚を貫通しないことを目視で確認する**

**判定基準:** SpringBone 定義を持つ VRM を 3 体以上用意し、そのすべてで座り動作中に破綻しないこと。うち 1 体でもモデル固有の手当てが必要になった場合は、その手当ての量を記録して再判断する。

副次的に、SpringBone 定義を持たない VRM でボーン名からの推測がどこまで通用するかも測る。

### PoC-2 — AssetBundle 経路

別プロジェクトで焼いた AssetBundle を Yuni 側プレイヤーで読み、以下を確認する。

- lilToon / MToon マテリアルが正しく描画されるか
- MagicaCloth2 コンポーネントの設定が保持され、プレイヤー側のアセンブリで解決されるか
- Unity バージョンをずらしたときに何が起きるか（読めないのか、静かに壊れるのか）

### PoC-3 — 透過マスコットウィンドウ

- UniWindowController の透過・クリックスルー・最前面が **IL2CPP ビルド**で動くか
- モデルの外接矩形まで窓を詰める処理（moca F-13-4）を Unity 側でどう実装するか
- 高 DPI・マルチモニタでの座標系の扱い

---

## 8. 判断

**PoC-1 が通ることを条件に、Yuni を moca の後継として開発する。**

- PoC-1 が通った場合 — 要件定義（`requirements.md`）に従って着手する
- PoC-1 が通らなかった場合 — Unity 化は moca に対する優位を持たない。ADR-0017 の判断が正しかったことになる。その場合の代替は「Unity Editor 拡張で衣装にコライダを仕込んだ VRM を書き出すオフライン変換ツール」へ縮退させることであり、moca 本体は無改造で済む

**PoC の結果は ADR として記録すること。** 通っても通らなくても、その判断は後から必ず問い直される。

---

## 付録: 参照

- [moca ADR-0017: moca は VRM と PMX に留まり、Unity 資産を扱わない](https://github.com/t1nyb0x/moca/blob/main/docs/adr/0017-vrm-pmx-only.md)
- [moca 要件定義書](https://github.com/t1nyb0x/moca/blob/main/docs/requirements.md)
- [MagicaCloth2 — Runtime Construction](https://magicasoft.jp/en/mc2_runtime_build/)
- [UniVRM](https://github.com/vrm-c/UniVRM)
- [UniWindowController](https://github.com/kirurobo/UniWindowController)
- [Unity Asset Store Terms of Service and EULA](https://unity.com/legal/as-terms)
