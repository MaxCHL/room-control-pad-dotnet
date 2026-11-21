using RoomControl.Core;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;
using RoomControl.Sim;
using RoomControl.Sim.Services;

// ---------------------------------------------------------------------------------------
// Nestay 智慧旅宿 — 客房 Pad 中控台 x 房間 Gateway x 後端服務 模擬
// 純軟體模擬：MQTT 語意由 InMemoryMessageBus 提供，無真實硬體、無真實 broker。
// ---------------------------------------------------------------------------------------

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string roomId = "1205";
const string secondaryFacilityId = "gym-1";
const string restaurantId = "sky-bistro";

static string NewId() => Guid.NewGuid().ToString("N")[..12];

void PrintHeader(string title)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine(title);
    Console.WriteLine(new string('=', 78));
}

void PrintSection(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('-', 78));
    Console.WriteLine(title);
    Console.WriteLine(new string('-', 78));
}

void PrintPadDeviceSnapshot(SimulatedPad pad)
{
    Console.WriteLine("  [Pad 畫面] 裝置現況：");
    foreach (var state in pad.DeviceCache.Values.OrderBy(s => s.DeviceId))
    {
        var line = state switch
        {
            LightState l => $"    - 燈光[{l.Zone}] {l.DeviceId}: on={l.IsOn}, 亮度={l.BrightnessPercent}%, 色溫={l.ColorTemperatureKelvin}K",
            CurtainState c => $"    - 窗簾 {c.DeviceId}: {c.Position}",
            AirconState a => $"    - 空調 {a.DeviceId}: on={a.IsOn}, {a.TargetTemperatureC}°C, 模式={a.Mode}, 風速={a.FanSpeed}",
            TvState t => $"    - 電視 {t.DeviceId}: on={t.IsOn}, 音量={t.Volume}, 頻道={t.Channel}",
            DoorSignState d => $"    - 門牌 {d.DeviceId}: {d.Mode}",
            _ => $"    - {state.DeviceId}: {state.Type}"
        };
        Console.WriteLine(line);
    }
    Console.WriteLine($"  [Pad 畫面] Gateway 在線狀態: {(pad.GatewayOnline ? "Online" : "Offline")}");
}

PrintHeader("Nestay 智慧旅宿 — 客房 Pad 中控台模擬（軟體 MQTT 層，無真實硬體/broker）");

// --- Wiring: message bus, device driver, room inventory, gateway, pad, backend services ---
var bus = new InMemoryMessageBus();
var driver = new SimulatedDeviceDriver();

var inventory = new RoomDeviceInventory
{
    RoomId = roomId,
    LightZoneDeviceIds = new Dictionary<string, string>
    {
        ["main"] = "light-main",
        ["bed"] = "light-bed",
        ["bath"] = "light-bath",
        ["night"] = "light-night"
    },
    CurtainDeviceId = "curtain-1",
    AirconDeviceId = "aircon-1",
    TvDeviceId = "tv-1",
    DoorSignDeviceId = "doorsign-1"
};

var gateway = new SimulatedRoomGateway(inventory, bus, driver);
var pad = new SimulatedPad(roomId, bus);
var robotDispatch = new RobotDispatchService(bus, robotIds: ["robot-01", "robot-02"], pickupTimeout: TimeSpan.FromMilliseconds(600));
var facilityService = new FacilityOccupancyService(bus);
var restaurantQueue = new RestaurantQueueService(bus, restaurantId, "Sky Bistro 空中餐廳");
var guestServices = new GuestServicesService(bus);

// =========================================================================================
// 1. Gateway 開機 + Pad 連線 → 載入 retained 房間現況
// =========================================================================================
PrintSection("1. Gateway 開機 + Pad 連線 → 載入 retained 房間現況");

await gateway.StartAsync();
await guestServices.StartAsync();
await robotDispatch.StartAsync();
await restaurantQueue.StartAsync();
await facilityService.RegisterFacilityAsync(secondaryFacilityId, "健身中心", capacity: 20, initialCount: 4);

await pad.ConnectAsync(); // subscribes after retained topics already exist -> receives current state immediately
await pad.WatchFacilityAsync(secondaryFacilityId);

Console.WriteLine("  Pad 連線完成，以下為連線瞬間即拿到的 retained 現況（燈關/空調待機/電視關/窗簾關）：");
PrintPadDeviceSnapshot(pad);

// =========================================================================================
// 2. 房間控制：情境「閱讀」→ 個別調光 → 空調 → 電視 → 窗簾
// =========================================================================================
PrintSection("2. 房間控制：情境「閱讀」(多裝置序列化套用) → 個別調光 → 空調 → 電視 → 窗簾");

await pad.SendSceneAsync(new SceneSetCommand { CommandId = NewId(), RoomId = roomId, Scene = SceneId.Reading, IssuedAtUtc = DateTimeOffset.UtcNow });
Console.WriteLine("  已送出情境「閱讀」，套用後現況：");
PrintPadDeviceSnapshot(pad);

Console.WriteLine("  個別調整：主燈調亮 -> 60%、空調設 22°C 強風、電視開機轉 8 台、窗簾開啟");
await pad.SendDeviceCommandAsync(new LightSetCommand { CommandId = NewId(), RoomId = roomId, DeviceId = "light-main", IssuedAtUtc = DateTimeOffset.UtcNow, BrightnessPercent = 60 });
await pad.SendDeviceCommandAsync(new AirconSetCommand { CommandId = NewId(), RoomId = roomId, DeviceId = "aircon-1", IssuedAtUtc = DateTimeOffset.UtcNow, IsOn = true, TargetTemperatureC = 22, Mode = AirconMode.Cool, FanSpeed = 4 });
await pad.SendDeviceCommandAsync(new TvSetCommand { CommandId = NewId(), RoomId = roomId, DeviceId = "tv-1", IssuedAtUtc = DateTimeOffset.UtcNow, IsOn = true, Volume = 35, Channel = 8 });
await pad.SendDeviceCommandAsync(new CurtainSetCommand { CommandId = NewId(), RoomId = roomId, DeviceId = "curtain-1", IssuedAtUtc = DateTimeOffset.UtcNow, Position = CurtainPosition.Open });
PrintPadDeviceSnapshot(pad);

// =========================================================================================
// 3. 線上點餐 → 送餐機器人端到端（含機器人忙碌排隊 / 逾時未取餐兩種失敗處置）
// =========================================================================================
PrintSection("3. 線上點餐 → 送餐機器人端到端（3 筆訂單 / 2 台機器人：正常送達 + 忙碌排隊 + 逾時返程）");

// order-A1 / order-B1 immediately claim the two idle robots; order-C1 arrives while both are
// busy and must queue, then gets served once a robot frees up.
var autoConfirmOrderIds = new HashSet<string> { "order-A1", "order-C1" }; // order-B1 is deliberately left uncollected to trigger the timeout branch
var orderSettled = new Dictionary<string, TaskCompletionSource>
{
    ["order-A1"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
    ["order-B1"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
    ["order-C1"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
};

pad.NotificationReceived += async (notification, ct) =>
{
    if (notification.Category != "Order" || notification.OrderId is null)
    {
        return;
    }

    if (notification.OrderStatus == OrderStatus.ArrivedAtDoor && autoConfirmOrderIds.Contains(notification.OrderId))
    {
        Console.WriteLine($"  [Pad 動作] 房客點擊「已取餐」，確認取件 {notification.OrderId}");
        await pad.SendServiceRequestAsync(new ConfirmPickupRequest
        {
            RequestId = NewId(),
            RoomId = roomId,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            OrderId = notification.OrderId
        }, ct).ConfigureAwait(false);
    }

    if (notification.OrderStatus is OrderStatus.Delivered or OrderStatus.TimedOutReturning or OrderStatus.Failed or OrderStatus.Cancelled)
    {
        orderSettled[notification.OrderId].TrySetResult();
    }
};

await pad.SendServiceRequestAsync(new FoodOrderRequest
{
    RequestId = "order-A1",
    RoomId = roomId,
    RequestedAtUtc = DateTimeOffset.UtcNow,
    Items = [new FoodOrderItem { Name = "夜宵拉麵", Quantity = 1 }]
});
await pad.SendServiceRequestAsync(new FoodOrderRequest
{
    RequestId = "order-B1",
    RoomId = roomId,
    RequestedAtUtc = DateTimeOffset.UtcNow,
    Items = [new FoodOrderItem { Name = "水果拼盤", Quantity = 1 }]
});
await pad.SendServiceRequestAsync(new FoodOrderRequest
{
    RequestId = "order-C1",
    RoomId = roomId,
    RequestedAtUtc = DateTimeOffset.UtcNow,
    Items = [new FoodOrderItem { Name = "礦泉水", Quantity = 2 }]
});

await Task.WhenAll(orderSettled.Values.Select(t => t.Task));
Console.WriteLine("  三筆訂單皆已結束（order-A1/C1 正常送達，order-B1 逾時未取由機器人折返並通知櫃檯）。");

// =========================================================================================
// 4. 設施即時人數：健身房人數更新推播到 Pad、尖峰警示
// =========================================================================================
PrintSection("4. 設施即時人數：健身房人數更新推播到 Pad、尖峰警示");

await facilityService.UpdateOccupancyAsync(secondaryFacilityId, newCount: 9);
await facilityService.UpdateOccupancyAsync(secondaryFacilityId, newCount: 17); // 17/20 = 85% -> 尖峰警示

// =========================================================================================
// 5. 餐廳排隊：線上取號 → 叫號
// =========================================================================================
PrintSection("5. 餐廳排隊：線上取號排隊 → 叫號");

var otherRoomTicketService = restaurantQueue; // same restaurant queue shared across rooms in a real hotel
var ticket = await restaurantQueue.TakeTicketAsync(roomId, partySize: 2);
Console.WriteLine($"  Pad 取號成功：{ticket.TicketNumber}（{ticket.PartySize} 位）");
await restaurantQueue.TakeTicketAsync("1310", partySize: 4); // another room's guest, ahead in the queue for the call-next demo below

var firstCalled = await restaurantQueue.CallNextAsync();
Console.WriteLine($"  餐廳叫號：{firstCalled?.TicketNumber}（房號 {firstCalled?.RoomId}）");
var secondCalled = await restaurantQueue.CallNextAsync();
Console.WriteLine($"  餐廳叫號：{secondCalled?.TicketNumber}（房號 {secondCalled?.RoomId}）");

// =========================================================================================
// 6. 情境「離房」一鍵關閉 + 快速退房
// =========================================================================================
PrintSection("6. 情境「離房」一鍵關閉（關燈+關電視+空調節能+關窗簾+DND） + 快速退房");

await pad.SendSceneAsync(new SceneSetCommand { CommandId = NewId(), RoomId = roomId, Scene = SceneId.Away, IssuedAtUtc = DateTimeOffset.UtcNow });
PrintPadDeviceSnapshot(pad);

await pad.SendServiceRequestAsync(new HousekeepingRequest
{
    RequestId = NewId(),
    RoomId = roomId,
    RequestedAtUtc = DateTimeOffset.UtcNow,
    Kind = GuestRequestKind.ExpressCheckout
});

// =========================================================================================
// 7. Gateway 離線（Last Will）→ Pad 顯示離線；冪等：重送同一 CommandId 不重複執行
// =========================================================================================
PrintSection("7. Gateway 離線（Last Will Testament）→ Pad 顯示離線 + 指令冪等測試");

Console.WriteLine("  模擬 Gateway 斷電/斷網（未正常斷線）...");
await gateway.SimulateCrashAsync();
Console.WriteLine($"  [Pad 畫面] Gateway 在線狀態（LWT 觸發後）: {(pad.GatewayOnline ? "Online" : "Offline")}");

Console.WriteLine();
Console.WriteLine("  冪等測試：對同一顆床頭燈重送完全相同的 CommandId（模擬 QoS1 重送）");
var dimCommand = new LightSetCommand
{
    CommandId = NewId(),
    RoomId = roomId,
    DeviceId = "light-bed",
    IssuedAtUtc = DateTimeOffset.UtcNow,
    IsOn = true,
    BrightnessPercent = 50,
    ColorTemperatureKelvin = 3000
};
await pad.SendDeviceCommandAsync(dimCommand);
Console.WriteLine("  ↑ 第一次送出（應套用並回 ACK success, wasDuplicate=false）");
await pad.SendDeviceCommandAsync(dimCommand);
Console.WriteLine("  ↑ 第二次重送相同 CommandId（應回 ACK wasDuplicate=true，裝置狀態不變）");

// =========================================================================================
// 8. 收尾摘要
// =========================================================================================
PrintSection("8. 模擬結束摘要");
Console.WriteLine($"  房間 {roomId} 裝置總數: {pad.DeviceCache.Count}");
Console.WriteLine($"  已處理訂單: {string.Join(", ", orderSettled.Keys)}");
Console.WriteLine($"  健身中心目前人數: {(pad.FacilityCache.TryGetValue(secondaryFacilityId, out var gym) ? $"{gym.CurrentCount}/{gym.Capacity}" : "unknown")}");
Console.WriteLine("  模擬完成，程式即將結束。");
