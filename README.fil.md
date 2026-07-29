# DeleteAudit

[简体中文](README.md) · [English](README.en.md) · **Filipino**

> Open-source na tool para tingnan at ayusin ang mga log sa Windows. **Alpha / eksperimental.**

Ang DeleteAudit ay isang open-source na tool para sa Windows na ginagamit sa pagtingin at pag-aayos ng mga log. Kaya nitong mag-import ng suportadong local log file, tulungan kang basahin ang resulta ng import, at — kapag ikaw mismo ang nagbukas nito — tumanggap, mag-imbak at magpabasa ng live na log sa sarili mong makina. Alpha pa ang proyekto: maganda itong pang-aral, pang-test, at pansubok ng workflow, at **hindi ito dapat ituring na kumpleto o production-grade na forensic system**.

## Ano ito

Itinatala ng Windows sa mga system log nito ang mga pangyayari gaya ng "nabura ang file na ito", pero kalat-kalat, hilaw, at mahirap basahin ang mga log na iyon. Kinukuha ng DeleteAudit ang **isang log file na ikaw mismo ang pumili**, at ginagawa itong listahan na kayang basahin: kailan, aling file, aling program, aling user.

Tool ito para **tumingin at mag-ayos**. Hindi ito panangga. Hindi nito mapipigilan ang pagbura, at hindi rin nito maibabalik ang nabura nang file.

## Mga pangunahing gamit

- **Offline import** — isang `.xml` o `.evtx` na log file sa bawat pagkakataon, ikaw ang pumipili.
- **Tingnan at ayusin** — dumaan sa mga na-import na resulta ayon sa oras, path at status, at buksan ang mga delete event at ang raw na ebidensiya ng mga ito.
- **Live ingestion (Phase 2B)** — pagkatapos mong pindutin ang start sa live na pahina, nagsu-subscribe ito nang read-only sa mga log channel na nandiyan na sa makina mo. **Mula sa sandaling simulan mo, isinusulat sa lokal na SQLite database ang raw XML, resulta ng pag-parse at klasipikasyon, at kaugnay na live evidence ng bawat suportadong event na natanggap**, at **nananatili** ang mga detalyeng matagumpay na naitala kahit itigil o isara mo na ang app. May naiitala ring session summary.
- **Live history at derived analysis** — naka-page ang pagtingin sa naitalang live session, record, diagnostic at limitadong raw-XML preview; kapag hiniling mo, muling pini-parse ang napiling session gamit ang kaparehong deterministic correlation, delete-session at risk rules ng offline path.
- **Hiwalay na live canonical projection** — kapag hayagan mo itong pinatakbo, inilalagay ang eligible na live evidence sa live-owned na mga table ng migration `0005`, kasama ang `live_evidence_id`, hiwalay na epoch, siksik na live sequence at continuity hash. Hindi nito sinusulatan, ginagaya o ikinakabit ang offline event table, identity, sequence o hash chain.
- **Talaan ng import** — bawat import ay may talaan at isang manifest file, para matiyak mo kung ano talaga ang pumasok.

## Para kanino ito

**Bagay ito kung** gusto mong makita kung ano talaga ang hitsura ng mga delete-related log ng Windows, kailangan mong gawing nababasang listahan ang isang log file, o gusto mong mag-aral o makatulong sa ganitong klaseng tool.

**Hindi pa ito bagay kung** kailangan mo ito para sa produksiyon o sa totoong imbestigasyon, kailangan mong pigilan ang aksidenteng pagbura o harangin ang umaatake, o naghahanap ka ng installer na i-download at gamitin agad.

## Kasalukuyang kalagayan at limitasyon

- Yugto: **Alpha / eksperimental**, kasalukuyang naipatupad hanggang **Phase 2B**.
- Sistema: **Windows 10 / Windows 11**.
- Runtime: **.NET 8**.
- **Hindi ito kumpleto o production-grade na forensic system**, at hindi ito sinusukat sa pamantayan ng komersiyal na digital forensics na produkto.
- Hindi nito mapipigilan ang aksidenteng pagbura, at hindi rin nito mahaharang ang determinadong umaatake o ang pakikialam sa ebidensiya.
- **May tunay nang viewer page para sa live history, derived correlation/session/risk analysis, at canonical projection.** Read-only ang derived analysis at hindi ito isinusulat pabalik. Sa live-owned na `0005` table lang sumusulat ang canonical projection. Hindi ipinapanggap ng alinman na offline import ang live data at hindi ito tahimik na idinadagdag sa offline na “delete events” o “raw evidence” page.
- **Kailangang hayagang ilapat ng developer o operator ang migration `0005`.** Hindi gumagawa o nagmi-migrate ng schema ang runtime. Kapag wala ang `0005`, live canonical projection page lang ang unavailable; gumagana pa rin ang offline, live preview, history at derived-analysis page.
- **May nakapirming deadline ang batch, pero hindi ito mahigpit na garantiya sa oras.** Agad na pumapasok sa persistence ang batch kapag umabot sa 64 record. Ang kulang na batch ay karaniwang nakaiskedyul para sa persistence mga limang segundo matapos pumasok ang unang record sa bakanteng batch; hindi inuulit ng mga kasunod na record ang deadline. Maaaring mas mahuli ang pagkumpleto dahil sa pag-iskedyul ng operating system at thread, SQLite I/O, o fault.
- **Puwede pa ring may mga puwang.** Kapag biglang namatay ang proseso, maaari pa ring mawala ang hanggang 63 na hindi pa na-commit na record; nag-iiwan din ng puwang ang pag-apaw ng queue, sobrang laking event, o bigong pagsulat. Ang session na walang completion record ay nangangahulugang hindi maayos na natapos ang capture na iyon.
- **Isang beses lang sinusubukang i-save ang completion record at walang awtomatikong retry.** Kapag nabigo iyon, `Error` ang ipinapakita ng session; nananatili ang mga record na matagumpay nang na-commit.
- **Walang signature, walang external anchoring, at walang tamper-proof na garantiya.** Makatutulong ang live-owned continuity hash na makita ang naputol na pagkakasunod o aksidenteng pagbabago, pero kayang buuin muli ang buong chain ng sinumang may write access sa database. Hindi tamper-proof na medium ang SQLite.
- **Source code lamang** ang inilalabas ng repository na ito. **Walang** handang gamitin at nakapirmang (signed) Windows installer.
- Pinakahuling beripikasyon: **278 unit test, 184 integration test, 462 lahat, dalawang magkasunod na run na pasado lahat**, na may build na 0 warning at 0 error.

Nasa [`docs/`](docs/) ang mga talaan ng bawat yugto, ang pangkalahatang disenyo, at ang threat model.

## Privacy at mga hangganan sa seguridad

Pakibasa nang maayos ang bahaging ito:

- **Walang tahimik na ina-upload.** Hindi nagpapadala ang DeleteAudit ng data mo kahit saan sa internet.
- **Hindi ito nagbabasa ng live na log bilang default.** May sinusubscribean lang na channel kapag ikaw mismo ang pumindot ng start.
- **Hindi ito kumokonekta sa remote na Windows event log** — sa mga channel lang na nandiyan na sa makinang ito.
- **Hindi ito naghahanap o nag-e-enumerate ng mga network location**, at hindi rin nito nililibot ang mga drive mo.
- **Hindi ito nag-i-install ng Sysmon, hindi nagbabago ng audit policy, hindi humahawak sa registry, hindi humihingi ng administrator rights, at hindi nananatili sa background.**
- **Hindi ito nag-iimbak at hindi rin humihingi ng anumang network credential.**
- May ilang panloob na system path (device path, gaya ng nagsisimula sa `\\?\` o `\\.\`) na **tinatanggihan agad** at hindi puwedeng i-import.

### Tungkol sa file na nasa network share

Kung nasa network share ang piniling file (halimbawa `\\server\share\log.evtx`), paghiwalayin ang dalawang bagay:

Kapag nag-browse o pumili ka ng network share sa file picker ng Windows, **maaaring nakakonekta na ang Windows sa share na iyon** at nasuri na kung umiiral ang path o ang file. Ang confirmation na lumalabas pagkatapos ay kumokontrol lang kung magpapatuloy ba ang **DeleteAudit** sa pagbasa at pag-import. Kapag pinili mong Cancel, natitigil ang susunod na gagawin ng DeleteAudit, pero **hindi nito maibabalik o mababawi** ang anumang nagawa na ng file picker ng sistema.

Naka-default sa Cancel ang confirmation, at Cancel din ang Escape. Sa bawat network share na pipiliin mo, tatanungin ka ulit — hindi kailanman inaalala ang naunang sagot. **Ang pinakamadali at pinakaligtas ay kopyahin muna ang file sa sarili mong makina**, saka i-import nang lokal.

Para malinaw: **hindi** ito katumbas ng "wala talagang mangyayaring network access". Kapag kumpirmado mo nang magpatuloy, ang pagbasa sa shared file na iyon ay talagang dumadaan sa network.

## Paano patakbuhin o paano makatulong

Sa ngayon, ikaw ang magbi-build mula sa source. Kailangan mo ng Windows 10/11, ng [.NET 8 SDK](https://dotnet.microsoft.com/download), at ng Git.

```bash
git clone <repository-url>
cd DeleteAudit
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Patakbuhin ang viewer:

```bash
dotnet run --project src/DeleteAudit.Viewer
```

Nananatili sa loob ng `artifacts\` na folder ng repository ang data at output; walang isinusulat sa labas ng checkout. Nasa [CONTRIBUTING.md](CONTRIBUTING.md) ang mga panuntunan sa build, sa test, at sa mga direktoryo.

## Pag-uulat ng problema sa seguridad

Pakigamit ang **GitHub Private Vulnerability Reporting** ng repository na ito (Security → Report a vulnerability) sa halip na magbukas ng pampublikong issue. Nasa [SECURITY.md](SECURITY.md) ang buong patakaran.

Kapag nag-uulat, **huwag** isama ang totoong log data, totoong pangalan ng makina, o totoong username — mas mabuti ang gawa-gawang halimbawa.

## Pag-ambag at lisensya

Malugod na tinatanggap ang mga issue at pull request. Basahin muna ang [CONTRIBUTING.md](CONTRIBUTING.md) at ang [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

Gumagamit ang proyektong ito ng **MIT License**. Puwede mo itong gamitin sa personal o komersiyal na layunin, at puwede mo rin itong baguhin at ipamahagi, basta't panatilihin mo ang lisensya at ang copyright notice. Nasa [LICENSE](LICENSE) ang buong teksto.
