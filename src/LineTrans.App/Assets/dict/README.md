# 内置离线词库

本目录下的 `core.tsv`（4 万常用词）与 `lemma.tsv`（约 10 万条词形还原，如 ran → run）是
PC 端内置的离线词库快照，由 `LineTrans.Dictionary` 的 `LocalDictionary` 在运行时加载。
输出路径为 `<App 目录>\dict\core.tsv` 与 `<App 目录>\dict\lemma.tsv`
（见 `LineTrans.App.csproj` 里的 `Content Update ... Link="dict\..."`）。

## 来源与许可

- 词库数据来自 [ECDICT](https://github.com/skywind3000/ECDICT)（MIT 许可），经脚本加工成 tsv；
  与安卓端 `app/src/main/assets/dict/` 是同一份数据。
- **代码遵循 AGPL-3.0-or-later，词典数据部分仍遵循 MIT**；再分发时请一并保留这段说明。

## 维护须知

- 改这份 tsv 会同时影响 PC 端运行时词库与 `tests\LineTrans.Dictionary.Tests`
  （测试工程用 `None Include ... Link="data\..."` 指向同一份文件，不另存副本）。
- 改完请重跑 `tests\LineTrans.Dictionary.Tests`，确认 201 项自测仍全部通过。
