# アーキテクチャ決定記録（ADR）

決定そのものと、その理由を記録する。**根拠の正は ADR 側**であり、要件定義書やアーキテクチャ文書はその索引である。

## 書く基準

- **後から必ず問い直される判断**を書く。「なぜこうなっているのか」が半年後に思い出せないもの
- 却下した代替案とその理由を必ず書く。**採らなかった道こそが後から蒸し返される**
- 影響（できなくなること）を書く。利点だけを書いた ADR は判断の記録ではなく宣伝である

## 書かない基準

- 自明なもの、いつでも安く覆せるもの
- 単なる手順やコーディング規約

## 状態

| 状態 | 意味 |
|---|---|
| 提案 | 議論中。まだ従わなくてよい |
| 採用 | 現行の決定。これに従う |
| 却下 | 検討したが採らなかった。**記録として残す** |
| 廃止 | かつて採用したが、もう従わない。後継の ADR 番号を明記する |

## 一覧

| # | 決定 | 状態 |
|---|---|---|
| [0001](0001-unity-for-cloth-and-emote.md) | クロスとエモートのために Unity で作り直す | 採用（**一部を 0004 で改訂**） |
| [0002](0002-unity-6000-0-lts.md) | Unity 6000.0 LTS に固定する | 採用 |
| [0003](0003-spcr-as-default-cloth-backend.md) | クロスは SPCR JointDynamics を既定とし、MagicaCloth2 は対応予定に置く | 採用 |
| [0004](0004-unitypackage-first-coexist-with-moca.md) | Yuni の主目的を unitypackage 配布モデルに置き、moca と恒久共存する | 採用 |

## moca からの継承

Yuni は moca と**棲み分けの違う姉妹製品**である（[ADR-0004](0004-unitypackage-first-coexist-with-moca.md)）。moca = VRM、Yuni = unitypackage 配布モデル。両者は恒久的に共存する。

**moca の ADR-0001〜0017 は、明示的に覆すまで有効な背景として扱う。** 仕様の正は moca 側に置き続けること（要件定義書 0 章）。

とくに次の 2 つは Yuni の出発点そのものであり、読むこと。

- [moca ADR-0015](https://github.com/t1nyb0x/moca/blob/main/docs/adr/0015-pmx-via-third-party-loader.md) — PMX を実験的対応に留めた判断。**標準の無いものを追うと保守が続かない**という教訓は Yuni でも生きている
- [moca ADR-0017](https://github.com/t1nyb0x/moca/blob/main/docs/adr/0017-vrm-pmx-only.md) — moca は VRM と PMX に留まり Unity 資産を扱わないという決定。**Yuni はこの ADR が「案 2」として退けた選択肢そのものである**
