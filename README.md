# Nestay 智慧旅宿 — 客房 Pad 中控台模擬

> **免責聲明**：本專案為技術展示，使用虛構旅宿連鎖「Nestay 智慧旅宿」與虛構資料，
> 與任何真實企業、飯店、品牌或產品無關。所有裝置、房號、訂單、機器人均為模擬資料。

純軟體模擬：飯店客房 **Pad 中控台 ↔ 房間 Gateway ↔ 後端服務** 之間以 **MQTT 語意**溝通。
沒有真實硬體、沒有真實 MQTT broker——`InMemoryMessageBus` 在記憶體中重現本專案需要的
MQTT 行為（topic 階層、retained 訊息、Last Will and Testament、pub/sub 派發），讓整套
互動可以單一 console process 內完整跑一遍並列印過程 log。

## 中控台介面 Dashboard

可操作的 Pad 中控台介面示意(場景、燈光、空調、窗簾、電視、環境、送餐機器人配送流程、
設施人數、餐廳排隊)、分層架構圖(軟體 MQTT 層 vs 裝置 Zigbee 層)與送餐流程圖,
放在 [`docs/index.html`](docs/index.html)。

> 專案開啟 GitHub Pages 後,可直接線上瀏覽:<https://maxchl.github.io/room-control-pad-dotnet/>
> (或把 `docs/index.html` 下載後用瀏覽器開啟。)

## 分層架構

```
┌─────────────┐   MQTT     ┌──────────────────┐   MQTT     ┌───────────────────┐
│ SimulatedPad │ <───────> │ IRoomMessageBus   │ <───────> │ SimulatedRoomGateway│
│ (客房 Pad)   │           │ (本模擬:記憶體匯流排│           │  (房間 Gateway)     │
└─────────────┘           │  正式環境:真實broker)│          └─────────┬──────────┘
                          └─────────┬─────────┘                     │
                                    │ MQTT                            │ IDeviceDriver
                    ┌───────────────┼───────────────┐                 ▼
                    ▼               ▼               ▼        SimulatedDeviceDriver
          RobotDispatchService  FacilityOccupancy  RestaurantQueue   (記憶體裝置狀態)
          GuestServicesService     Service            Service
              (後端服務)
```

- **軟體 MQTT 層**（本專案的重點、實際模擬的部分）：Pad、Gateway、後端服務三者只依賴
  `IRoomMessageBus` 這個抽象介面溝通，彼此互不知道對方的存在，全靠 topic 訂閱/發布。
  `InMemoryMessageBus`（`RoomControl.Sim/InMemoryMessageBus.cs`）是這個介面在本模擬中的
  實作，重現了：
  - Topic 階層與萬用字元比對（`+` 單層、`#` 多層，比對邏輯見其 `TopicMatches`）
  - Retained 訊息：發布時 `retain: true` 會保留最後一筆，日後訂閱者會立即收到
  - Last Will and Testament：`RegisterLastWill` + `DisconnectUngracefullyAsync` 模擬斷電/斷網
  - 正式環境會把這個介面換成真正的 MQTT client（例如 MQTTnet）接到真實 broker，
    Pad/Gateway/後端服務的程式碼完全不用改。（同作者另有以 MQTTnet 實作真實 MQTT 的
    智慧門鎖專案，可作為正式 broker 整合的參考，此處不重複贅述。）

- **裝置層抽象**（`IDeviceDriver`，`RoomControl.Core/Devices/IDeviceDriver.cs`）：Gateway
  收到 `.../set` 指令後，透過這個介面請裝置「動作」並回報新狀態。正式環境裡，不同裝置
  家族會透過不同實體匯流排：
  - **Zigbee**：燈光、窗簾、多數感測器（ZCL On/Off、Level Control、Color Control、
    Window Covering cluster；含 mesh join/pairing、無線電重送等韌體層行為）
  - **DALI**：建築化調光鎮流器
  - **HDMI-CEC / IR**：電視電源、音量、頻道、投放切換
  - **KNX / Modbus**：中央空調（VRV/VRF）
  本模擬**不模擬 Zigbee 等無線電細節**（那是韌體層），`SimulatedDeviceDriver`
  （`RoomControl.Sim/SimulatedDeviceDriver.cs`）只在記憶體中做「收到指令 → 裝置動作 →
  回報新狀態」這件事，每個分支都以註解標明對應的正式協定。

## 控制項清單

| 分類 | 項目 |
|---|---|
| 燈光 | 開關、亮度、色溫；情境（全開/閱讀/睡眠/夜燈/離房/勿擾）；分區（main/bed/bath/night） |
| 窗簾 | 開 / 合 / 停 |
| 空調 | 電源、溫度、模式（冷氣/暖氣/送風/除濕/自動）、風速 |
| 電視 | 電源、音量、頻道、投放來源 |
| 房門/門鈴 | 電子門牌：勿擾（DND）/ 請整理房間（MUR）/ 無 |
| 環境顯示 | 室內溫濕度、CO2、PM2.5（retained，連線即拿到現況） |
| 客房事務 | 清潔請求、備品補給、喚醒服務、帳單查詢、快速退房 |
| 求助 | 緊急 SOS |
| 送餐 | 線上點餐、送餐機器人端到端追蹤、取餐確認 |
| 設施 | 即時人數顯示（尖峰警示） |
| 餐廳 | 線上取號排隊、叫號 |

情境（Scene）由 `RoomControl.Core/SceneEngine.cs` 集中定義，每個情境會展開成多筆裝置指令，
並由 Gateway 在同一把鎖內**序列化套用**（不被其他指令插隊，見下方可靠性章節），例如「離房」= 關所有燈 +
關電視 + 空調節能（非直接斷電）+ 窗簾關 + 設定 DND。注意情境不是 all-or-nothing：若某個裝置在情境中途失敗，已套用的裝置不會回滾。

## Topic 設計

集中定義於 `RoomControl.Core/Messaging/RoomTopics.cs`：

| Topic | Retained | 用途 |
|---|:---:|---|
| `room/{roomId}/device/{deviceId}/state` | ✅ | 裝置狀態 |
| `room/{roomId}/device/{deviceId}/set` | | 裝置指令 |
| `room/{roomId}/device/{deviceId}/set/ack` | | 指令 ACK（含冪等重複旗標） |
| `room/{roomId}/scene/set` | | 情境指令 |
| `room/{roomId}/scene/set/ack` | | 情境 ACK |
| `room/{roomId}/env/state` | ✅ | 室內溫濕度/CO2/PM2.5 |
| `room/{roomId}/gateway/status` | ✅ + LWT | Gateway 上下線 |
| `room/{roomId}/service/order` | | 客房服務請求（點餐、確認取餐、清潔/備品/喚醒/帳單/退房/SOS，以 `$type` 區分） |
| `room/{roomId}/service/notify` | | 後端 → Pad 的狀態推播 |
| `robot/{robotId}/status` | ✅ | 送餐機器人狀態 |
| `hotel/facility/{facilityId}/occupancy` | ✅ | 設施即時人數 |
| `hotel/restaurant/{restId}/queue` | ✅ | 餐廳排隊狀態 |

`service/order` 刻意只用一個 topic 承載多種請求類型（點餐、取餐確認、房務類請求），
以 JSON 的 `$type` 判別式（`System.Text.Json` 的 `[JsonPolymorphic]`/`[JsonDerivedType]`）
路由到不同後端服務處理，這對應真實飯店常見「一個服務請求佇列、多種請求種類」的整合模式。

## 送餐機器人流程

線上點餐 → `RobotDispatchService` 接單 → 廚房準備 → 指派機器人 → 出發（en route）→
抵達房門 → 通知 Pad → 房客取餐確認 → 完成。狀態逐步透過 `service/notify` 串流回 Pad，
機器人自身狀態則透過 `robot/{robotId}/status`（retained）廣播。

**失敗處置（兩種都在 `RobotDispatchService.cs` 實作並於 Demo 中觸發）**：
- **機器人忙碌排隊**：`ReserveRobotOrEnqueueAsync` 找不到 Idle 機器人時把訂單放進
  `ConcurrentQueue`，待任一機器人釋出後由 `HandOffOrGoIdleAsync` 自動接手下一筆。
- **逾時未取餐**：`RunRobotDeliveryAsync` 以 `Task.WhenAny(取餐訊號, 逾時計時器)` 競賽；
  逾時獲勝時機器人狀態轉為 `Returning`（返回廚房），並印出 `[告警->櫃檯值班]` 訊息模擬
  通知櫃檯，同時透過 `service/notify` 通知房客。

Demo 腳本一次送出 3 筆訂單、只有 2 台機器人，刻意製造「其中一筆要排隊」；3 筆訂單中
2 筆會被 Pad 自動模擬「房客立即取餐」，1 筆刻意不取以觸發逾時分支——藉由監聽
`NotificationReceived` 事件，在收到「已抵達房門」通知的當下才送出取餐確認，而非用固定
延遲時間去賭時間差，避免時間敏感造成的 flaky demo。

## 可靠性設計

- **Retained 狀態**：Pad 一連上（`SimulatedPad.ConnectAsync`）就能立刻拿到裝置/環境/
  Gateway 現況，不用等下一次變更。
- **Last Will (LWT)**：Gateway 開機時向 bus 註冊「若我沒有正常斷線就幫我發布 offline」，
  `SimulateCrashAsync` 模擬斷電/斷網，驗證 Pad 端會看到 Gateway 轉為離線（見
  `SimulatedRoomGateway.StartAsync` 與 `InMemoryMessageBus.DisconnectUngracefullyAsync`）。
- **QoS1 概念 + 應用層冪等**：MQTT QoS1 只保證「至少送達一次」，可能重複——因此可靠性
  真正落在應用層：每個指令帶 `CommandId`，`SimulatedRoomGateway` 用
  `ConcurrentDictionary` 以（DeviceId, CommandId）記錄已處理過的裝置指令（情境指令另以
  CommandId 記錄），重送時直接回傳先前的 ACK（標記 `WasDuplicate=true`）而不重新套用，
  Demo 第 7 節會實際重送同一個 CommandId 驗證此行為。
  餐點訂單與房務請求則以 `RequestId`（訂單即 `OrderId`）去重：同一個 RequestId 重送，
  不會重複建單、重複保留機器人或重複排隊（去重紀錄存在記憶體，服務重啟後不保留）。
  處理中又收到同一個請求時，重複的那筆會立刻返回、不等待；若第一次處理後來失敗，系統會在背景
  代為重試**一次**（不受原請求的取消影響），所以「處理中剛好收到重送」不會讓請求遺失。這次背景重試
  若仍失敗，只會記錄錯誤、不再自動重試，需由用戶端重新送出。通知是「至少送達一次」：失敗後重試時，
  已收到通知的訂閱者可能再收到一次。
  送餐訂單在交給機器人流程（保留到機器人或排入佇列）時即視為受理完成，之後的通知或送達失敗
  不會因重送而重試，以免重複派單。
- **ACK**：Gateway 對每個裝置指令、情境指令都回覆 `CommandAck`（成功與否、是否為重複、
  訊息），Pad 訂閱 ack topic 並印出結果，讓「指令送出」與「指令生效」是兩件可分別驗證
  的事，不是發布就假設成功。
- **情境序列化套用**：一個情境展開的多筆裝置指令，在 Gateway 內以 `SemaphoreSlim` 序列化套
  用，不會有「情境套用到一半」被其他指令插隊觀察到的狀態。注意這是序列化而非
  all-or-nothing：若某個裝置在情境中途失敗，已套用的裝置不會回滾，Gateway 也不會發出情境 ACK。
- **併發正確性**：送餐機器人的「尋找 Idle 機器人並保留」與「機器人釋出後接手下一筆／
  轉為 Idle」是在同一把鎖（`RobotDispatchService._reservationLock`）內序列化的決策，避免
  多筆訂單同時搶機器人時發生「同一台機器人被兩筆訂單同時保留」的競態條件。

## Build / Run

前置需求：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（LTS，專案目標 `net10.0`）。

```bash
dotnet --version                  # 需為 10.x 以上
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build RoomControl.sln      # 0 Warning(s), 0 Error(s)
dotnet run --project RoomControl.Sim/RoomControl.Sim.csproj
```

程式會依序印出 8 段情境 log（開機/連線、房間控制、送餐端到端、設施人數、餐廳排隊、
離房情境、Gateway 離線與冪等測試、結尾摘要），全部跑完以 exit code 0 結束，過程沒有
未處理例外。訂單分派會因為背景工作的排程時間點不同而在每次執行間略有差異（例如哪筆
訂單先搶到機器人、哪筆需要排隊），但最終結果穩定：固定 2 筆送達成功、1 筆逾時折返。

## 測試

`RoomControl.Tests`（xUnit v3）涵蓋情境展開、Gateway 的冪等與序列化套用、送餐機器人
（成功、逾時折返、併發不重複指派）、餐廳排隊、設施人數與記憶體匯流排（retained/LWT/萬用字元）。
測試不使用長時間 sleep，以事件/狀態輪詢等待。

```bash
dotnet test
```

請在 repo 根目錄執行 `dotnet test`（`global.json` 會選用 Microsoft.Testing.Platform）；IDE 需啟用 MTP 支援，或改用專案內附的 VSTest 轉接器。

> `dotnet test` 在 .NET 10 SDK 以上走 Microsoft.Testing.Platform，已在根目錄 `global.json` 開啟。

## 專案結構

```
RoomControl.Core/              # 純模型與介面，不依賴任何模擬實作
  Models/                      # DeviceState 系列、RoomEnvState、FacilityOccupancyState、
                                # RestaurantQueueState、FoodOrder、RobotStatusSnapshot、
                                # RoomDeviceInventory、列舉
  Commands/                    # DeviceCommand 系列、SceneSetCommand、CommandAck、
                                # GuestServiceRequest 系列、ServiceNotification
  Messaging/                   # IRoomMessageBus、RoomTopics
  Devices/                     # IDeviceDriver
  SceneEngine.cs                # 情境 → 裝置指令展開邏輯

RoomControl.Sim/               # 模擬實作 + Demo 入口
  InMemoryMessageBus.cs         # IRoomMessageBus 的記憶體實作（retained/LWT/萬用字元）
  SimulatedDeviceDriver.cs      # IDeviceDriver 的記憶體實作
  SimulatedRoomGateway.cs       # 房間 Gateway：狀態機、冪等、ACK、情境序列化套用
  SimulatedPad.cs                # 客房 Pad：訂閱快取、下指令、事件通知
  Services/
    RobotDispatchService.cs     # 送餐機器人派工、排隊、逾時折返
    FacilityOccupancyService.cs # 設施即時人數
    RestaurantQueueService.cs   # 餐廳排隊取號/叫號
    GuestServicesService.cs     # 清潔/備品/喚醒/帳單/退房/SOS
  Program.cs                    # Demo 腳本（8 段情境）
```

## 模擬 vs 正式環境的差異

| 項目 | 本模擬 | 正式環境 |
|---|---|---|
| MQTT broker | 記憶體內的 `InMemoryMessageBus` | 真實 broker（如 EMQX / Mosquitto），Pad/Gateway/後端各自是獨立 process 透過網路連線 |
| 裝置層 | `SimulatedDeviceDriver` 純記憶體狀態機 | Zigbee 協調器、DALI 閘道器、IR/CEC 控制器、KNX/Modbus 閘道器等實體介面 |
| QoS/重送 | 應用層以 CommandId 冪等模擬，bus 本身不做真正重送 | Broker 端真正實作 QoS1（至少一次，可能重複）與封包重送，應用層冪等仍然必要 |
| 冪等紀錄 | 各服務與 Gateway 的去重紀錄存在記憶體內，且不設上限、不淘汰。房務請求與送餐受理失敗時會移除紀錄，讓重送重試；處理中的重複請求立即返回，若第一次失敗則在背景代為重試一次（之後仍失敗就只記錄，需用戶端重送）。Gateway 則會快取失敗的裝置／情境 ACK，同一個 CommandId 重送時直接回覆該 ACK | 使用共用儲存（如 Redis 並設定到期時間），TTL 需涵蓋 broker 的重送窗口，避免記憶體無限成長 |
| 時間 | 以短暫 `Task.Delay` 模擬移動/等待時間 | 真實電梯/走廊移動時間、真實廚房準備時間 |

## 授權

本專案以 [MIT License](LICENSE) 授權。
