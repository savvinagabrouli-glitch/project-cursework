// Project/Cursework.Wpf/ViewModels/Waiter/WaiterWindowViewModel.cs
using Cursework.Application.Interfaces;
using Cursework.Application.Models;
using Cursework.Domains.Models;
using Cursework.Wpf.Models.HallLayout;
using Cursework.Wpf.Services.HallLayout;
using Cursework.Wpf.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace Cursework.Wpf.ViewModels.Waiter
{
    public class WaiterWindowViewModel : ViewModelBase
    {
        private readonly ITableService _tableService;
        private readonly IHallLayoutStorageService _layoutStorage;
        private readonly ICallWaiterService _callWaiterService;
        private readonly IOrderService _orderService;
        private readonly IOrderDetailsService _orderDetailsService;

        private readonly DispatcherTimer _notificationsTimer;
        private readonly CancellationTokenSource _pollingCts = new();

        private readonly List<DiningTable> _allTables = new();

        // Порядок зон по кругу: VIP → Bar → MainHall → Terrace → VIP
        private static readonly string[] ZoneCycle = { "VIP", "Bar", "MainHall", "Terrace" };

        public WaiterWindowViewModel(
            ITableService tableService,
            IHallLayoutStorageService layoutStorage,
            ICallWaiterService callWaiterService,
            IOrderService orderService,
            IOrderDetailsService orderDetailsService)
        {
            _tableService = tableService;
            _layoutStorage = layoutStorage;
            _callWaiterService = callWaiterService;
            _orderService = orderService;
            _orderDetailsService = orderDetailsService;

            _notificationsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _notificationsTimer.Tick += async (_, _) => await LoadNotificationsAsync();

            // стартовая зона — основной зал
            _selectedZone = "MainHall";

            NextZoneCommand = new RelayCommand(_ => SwitchZone(+1));
            PrevZoneCommand = new RelayCommand(_ => SwitchZone(-1));

            ToggleNotificationsCommand = new RelayCommand(_ => IsNotificationsOpen = !IsNotificationsOpen);

            RefreshNotificationsCommand = new RelayCommand(async _ => await LoadNotificationsAsync());
            OpenNotificationCommand = new RelayCommand(async param => await NavigateToNotificationAsync(param as WaiterNotificationItem));
            AcceptNotificationCommand = new RelayCommand(async param => await HandleNotificationAsync(param as WaiterNotificationItem));

            CreateOrderCommand = new RelayCommand(async _ => await CreateOrderAsync(), _ => HasSelectedTable && !HasActiveOrder);
            RefreshOrderCommand = new RelayCommand(async _ => await LoadActiveOrderForTableAsync(SelectedTableOnMap?.TableId));
            SaveOrderStatusCommand = new RelayCommand(async _ => await UpdateOrderStatusAsync(), _ => HasActiveOrder);
            UpdateItemStatusCommand = new RelayCommand(async param => await UpdateItemStatusAsync(param as WaiterOrderItem), param => param is WaiterOrderItem);

        }

        #region PUBLIC API

        /// <summary>
        /// Асинхронная инициализация: загрузка столов и раскладок.
        /// Вызвать из кода окна после создания VM.
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadTablesAsync();
            LoadZoneLayout();
            await LoadNotificationsAsync();
            _notificationsTimer.Start();
        }

        #endregion

        #region Zones + layout

        private string _selectedZone;
        public string SelectedZone
        {
            get => _selectedZone;
            set
            {
                if (Set(ref _selectedZone, value))
                {
                    Raise(nameof(SelectedZoneTitle));
                    LoadZoneLayout();
                }
            }
        }

        public string SelectedZoneTitle => SelectedZone switch
        {
            "MainHall" => "Основной зал",
            "Terrace" => "Терраса",
            "Bar" => "Бар",
            "VIP" => "VIP-зал",
            _ => "Зал"
        };

        private double _zoom = 1.0;
        public double Zoom
        {
            get => _zoom;
            set
            {
                var v = Math.Max(0.6, Math.Min(2.5, value));
                Set(ref _zoom, v);
            }
        }

        /// <summary>
        /// Столы на карте для текущей зоны (на основе активного пресета).
        /// </summary>
        public ObservableCollection<TableOnMapItem> TablesOnMap { get; } = new();

        private TableOnMapItem? _selectedTableOnMap;
        public TableOnMapItem? SelectedTableOnMap
        {
            get => _selectedTableOnMap;
            set
            {
                if (Set(ref _selectedTableOnMap, value))
                {
                    Raise(nameof(HasSelectedTable));
                    _ = LoadActiveOrderForTableAsync(value?.TableId);
                    UpdateCommandStates();
                }
            }
        }

        public bool HasSelectedTable => SelectedTableOnMap != null;

        private string? _layoutMessage;
        public string? LayoutMessage
        {
            get => _layoutMessage;
            set => Set(ref _layoutMessage, value);
        }

        public bool HasLayout => string.IsNullOrWhiteSpace(LayoutMessage);

        public ObservableCollection<string> OrderStatusOptions { get; } = new(new[]
        {
            "Preorder", "New", "Pending", "ReadyToPay", "Closed", "Cancelled"
        });

        public ObservableCollection<string> OrderItemStatusOptions { get; } = new(new[]
        {
            "Ordered", "Preparing", "Served", "Cancelled"
        });

        private Order? _activeOrder;
        private OrderDetailsDto? _activeOrderDetails;

        public ObservableCollection<WaiterOrderItem> ActiveOrderItems { get; } = new();

        public int? ActiveOrderId => _activeOrder?.Id;

        private string? _activeOrderStatus;
        public string? ActiveOrderStatus
        {
            get => _activeOrderStatus;
            set => Set(ref _activeOrderStatus, value);
        }

        private decimal _activeOrderTotal;
        public decimal ActiveOrderTotal
        {
            get => _activeOrderTotal;
            set => Set(ref _activeOrderTotal, value);
        }

        public bool HasActiveOrder => _activeOrder != null &&
            !string.Equals(_activeOrder.Status, "Closed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_activeOrder.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);

        private async Task LoadTablesAsync()
        {
            _allTables.Clear();
            var all = await _tableService.GetAllAsync();
            _allTables.AddRange(all);
        }

        /// <summary>
        /// Загружает раскладку для текущей зоны ИСКЛЮЧИТЕЛЬНО из активного пресета.
        /// Если активный не найден — берётся "Стандартный", затем первый по зоне.
        /// </summary>
        private void LoadZoneLayout()
        {
            TablesOnMap.Clear();
            LayoutMessage = null;

            // 1) Загружаем все пресеты
            var allPresets = _layoutStorage.LoadPresets() ?? new List<HallLayoutPresetModel>();

            // 2) Находим активный пресет для зоны через ActivePresetStore
            var activeIdOrName = _layoutStorage.GetActivePresetName(SelectedZone);

            HallLayoutPresetModel? preset = null;
            if (!string.IsNullOrWhiteSpace(activeIdOrName))
            {
                // пробуем искать по Id или по Name
                preset = allPresets.FirstOrDefault(p =>
                    string.Equals(p.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(p.Id, activeIdOrName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Name, activeIdOrName, StringComparison.OrdinalIgnoreCase)));
            }

            // 3) Фоллбэк: "Стандартный" для зоны
            if (preset == null)
            {
                preset = allPresets.FirstOrDefault(p =>
                    string.Equals(p.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Name, "Стандартный", StringComparison.OrdinalIgnoreCase));
            }

            // 4) Фоллбэк: любой активный по зоне
            if (preset == null)
            {
                preset = allPresets.FirstOrDefault(p =>
                    string.Equals(p.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase) &&
                    p.IsActive);
            }

            // 5) Фоллбэк: первый пресет для зоны
            if (preset == null)
            {
                preset = allPresets.FirstOrDefault(p =>
                    string.Equals(p.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase));
            }

            if (preset == null)
            {
                LayoutMessage = "Нет активного пресета для зоны";
                SelectedTableOnMap = null;
                return;
            }

            preset.Tables ??= new List<HallTableLayoutModel>();

            var tablesById = _allTables
                .Where(t => string.Equals(t.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.Id);

            foreach (var layout in preset.Tables.Where(t =>
                         string.Equals(t.Zone, SelectedZone, StringComparison.OrdinalIgnoreCase)))
            {
                if (!tablesById.TryGetValue(layout.TableId, out var table))
                    continue;

                TablesOnMap.Add(new TableOnMapItem
                {
                    TableId = table.Id,
                    TableName = table.Name,
                    ZoneCode = SelectedZone,
                    Seats = table.Seats,
                    Status = table.Status,
                    X = layout.X,
                    Y = layout.Y,
                    Model = layout.ModelType
                });
            }

            SelectedTableOnMap = TablesOnMap.FirstOrDefault();
            UpdateCommandStates();
        }

        private void SwitchZone(int delta)
        {
            // текущий индекс в цикле
            var currentIndex = Array.IndexOf(ZoneCycle, SelectedZone);
            if (currentIndex < 0) currentIndex = 0;

            var newIndex = (currentIndex + delta) % ZoneCycle.Length;
            if (newIndex < 0) newIndex += ZoneCycle.Length;

            SelectedZone = ZoneCycle[newIndex];
        }

        #endregion

        #region Orders

        private async Task LoadActiveOrderForTableAsync(int? tableId)
        {
            if (tableId == null || tableId <= 0)
            {
                ClearActiveOrder();
                return;
            }

            try
            {
                var orders = await _orderService.GetAllAsync(_pollingCts.Token);
                var active = orders
                    .Where(o => o.TableId == tableId)
                    .Where(o => !string.Equals(o.Status, "Closed", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(o.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(o => o.CreatedAt)
                    .FirstOrDefault();

                _activeOrder = active;
                ActiveOrderStatus = active?.Status;
                Raise(nameof(HasActiveOrder));
                UpdateCommandStates();

                if (active == null)
                {
                    ClearActiveOrder();
                    return;
                }

                await LoadOrderDetailsAsync(active.Id);
            }
            catch
            {
                ClearActiveOrder();
            }
        }

        private async Task LoadOrderDetailsAsync(int orderId)
        {
            ActiveOrderItems.Clear();
            _activeOrderDetails = null;

            try
            {
                _activeOrderDetails = await _orderDetailsService.GetAsync(orderId, _pollingCts.Token);
                if (_activeOrderDetails == null)
                    return;

                ActiveOrderTotal = _activeOrderDetails.TotalAmount;
                ActiveOrderStatus = _activeOrder?.Status ?? _activeOrderDetails.OrderStatus;

                foreach (var item in _activeOrderDetails.Guests.SelectMany(g => g.Items))
                {
                    ActiveOrderItems.Add(new WaiterOrderItem
                    {
                        ItemId = item.ItemId,
                        GuestIndex = item.GuestIndex,
                        DishName = item.DishName,
                        Quantity = item.Quantity,
                        Price = item.Price,
                        Status = item.Status,
                        Notes = item.Notes
                    });
                }
            }
            finally
            {
                Raise(nameof(HasActiveOrder));
                UpdateCommandStates();
            }
        }

        private void ClearActiveOrder()
        {
            _activeOrder = null;
            _activeOrderDetails = null;
            ActiveOrderItems.Clear();
            ActiveOrderTotal = 0m;
            ActiveOrderStatus = null;
            Raise(nameof(HasActiveOrder));
            UpdateCommandStates();
        }

        private async Task CreateOrderAsync()
        {
            if (!HasSelectedTable || SelectedTableOnMap == null)
                return;

            var order = new Order
            {
                TableId = SelectedTableOnMap.TableId,
                Status = "New"
            };

            await _orderService.AddAsync(order, _pollingCts.Token);
            await LoadActiveOrderForTableAsync(order.TableId);
        }

        private async Task UpdateOrderStatusAsync()
        {
            if (_activeOrder == null || string.IsNullOrWhiteSpace(ActiveOrderStatus))
                return;

            _activeOrder.Status = ActiveOrderStatus;
            await _orderService.UpdateAsync(_activeOrder, _pollingCts.Token);
            await LoadActiveOrderForTableAsync(_activeOrder.TableId);
        }

        private async Task UpdateItemStatusAsync(WaiterOrderItem? item)
        {
            if (item == null || _activeOrderDetails == null)
                return;

            var dtoItem = _activeOrderDetails.Guests
                .SelectMany(g => g.Items)
                .FirstOrDefault(i => i.ItemId == item.ItemId);

            if (dtoItem == null)
                return;

            dtoItem.Status = item.Status;
            await SaveOrderDetailsAsync();
        }

        private async Task SaveOrderDetailsAsync()
        {
            if (_activeOrderDetails == null)
                return;

            var saved = await _orderDetailsService.SaveAsync(_activeOrderDetails, _pollingCts.Token);
            _activeOrderDetails = saved;
            ActiveOrderItems.Clear();

            foreach (var item in saved.Guests.SelectMany(g => g.Items))
            {
                ActiveOrderItems.Add(new WaiterOrderItem
                {
                    ItemId = item.ItemId,
                    GuestIndex = item.GuestIndex,
                    DishName = item.DishName,
                    Quantity = item.Quantity,
                    Price = item.Price,
                    Status = item.Status,
                    Notes = item.Notes
                });
            }

            ActiveOrderTotal = saved.TotalAmount;
        }

        #endregion

        #region Notifications

        public ObservableCollection<WaiterNotificationItem> Notifications { get; } = new();

        private bool _isLoadingNotifications;

        private bool _isNotificationsOpen;
        public bool IsNotificationsOpen
        {
            get => _isNotificationsOpen;
            set => Set(ref _isNotificationsOpen, value);
        }

        public int UnreadNotificationsCount =>
            Notifications.Count(n => !n.IsRead);

        private WaiterNotificationItem? _selectedNotification;
        public WaiterNotificationItem? SelectedNotification
        {
            get => _selectedNotification;
            set => Set(ref _selectedNotification, value);
        }

        private async Task LoadNotificationsAsync()
        {
            if (_isLoadingNotifications)
                return;

            _isLoadingNotifications = true;

            try
            {
                var list = await _callWaiterService.GetAllAsync(_pollingCts.Token);
                var tablesById = _allTables.ToDictionary(t => t.Id, t => t);

                var unhandled = list
                    .Where(cw => !cw.IsHandled && cw.HandledAt == null)
                    .OrderByDescending(cw => cw.CreatedAt)
                    .ToList();

                Notifications.Clear();

                foreach (var cw in unhandled)
                {
                    tablesById.TryGetValue(cw.TableId, out var table);

                    Notifications.Add(new WaiterNotificationItem
                    {
                        Id = cw.Id,
                        TableId = cw.TableId,
                        OrderId = cw.OrderId,
                        ZoneCode = table?.Zone ?? cw.Table?.Zone ?? string.Empty,
                        TableName = table?.Name ?? cw.Table?.Name ?? $"Стол {cw.TableId}",
                        CreatedAt = cw.CreatedAt,
                        Type = cw.Type,
                        Title = "Вызов официанта",
                        Message = cw.Type,
                        IsRead = false
                    });
                }

                Raise(nameof(UnreadNotificationsCount));
            }
            finally
            {
                _isLoadingNotifications = false;
            }
        }

        private async Task HandleNotificationAsync(WaiterNotificationItem? notification)
        {
            if (notification == null)
                return;

            var updated = new CallWaiter
            {
                Id = notification.Id,
                TableId = notification.TableId,
                OrderId = notification.OrderId,
                CreatedAt = notification.CreatedAt,
                Type = notification.Type,
                IsHandled = true,
                HandledAt = DateTime.UtcNow
            };

            await _callWaiterService.UpdateAsync(updated, _pollingCts.Token);
            Notifications.Remove(notification);
            Raise(nameof(UnreadNotificationsCount));
        }

        private async Task NavigateToNotificationAsync(WaiterNotificationItem? notification)
        {
            if (notification == null)
                return;

            if (!string.IsNullOrWhiteSpace(notification.ZoneCode) &&
                !string.Equals(notification.ZoneCode, SelectedZone, StringComparison.OrdinalIgnoreCase))
            {
                SelectedZone = notification.ZoneCode;
            }

            SelectTableOnMap(notification.TableId);
            await LoadActiveOrderForTableAsync(notification.TableId);
        }

        private void SelectTableOnMap(int tableId)
        {
            var table = TablesOnMap.FirstOrDefault(t => t.TableId == tableId);
            if (table != null)
            {
                SelectedTableOnMap = table;
                table.IsSelected = true;
                return;
            }

            SelectedTableOnMap = TablesOnMap.FirstOrDefault();
        }

        #endregion

        #region Commands

        public ICommand NextZoneCommand { get; }
        public ICommand PrevZoneCommand { get; }

        public ICommand ToggleNotificationsCommand { get; }

        public ICommand RefreshNotificationsCommand { get; }
        public ICommand OpenNotificationCommand { get; }
        public ICommand AcceptNotificationCommand { get; }

        public ICommand CreateOrderCommand { get; }
        public ICommand RefreshOrderCommand { get; }
        public ICommand SaveOrderStatusCommand { get; }
        public ICommand UpdateItemStatusCommand { get; }

        #endregion

        private void UpdateCommandStates() => CommandManager.InvalidateRequerySuggested();

        #region Nested models

        /// <summary>
        /// Элемент стола на карте для официанта (почти как в HallMapViewModel).
        /// </summary>
        public class TableOnMapItem : ViewModelBase
        {
            public int TableId { get; set; }
            public string TableName { get; set; } = "";
            public string ZoneCode { get; set; } = "";

            private double _x;
            public double X
            {
                get => _x;
                set => Set(ref _x, value);
            }

            private double _y;
            public double Y
            {
                get => _y;
                set => Set(ref _y, value);
            }

            private string _model = "Single"; // Single / Multi
            public string Model
            {
                get => _model;
                set => Set(ref _model, value);
            }

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set => Set(ref _isSelected, value);
            }

            public int Seats { get; set; }
            public string Status { get; set; } = "";
        }

        public class WaiterOrderItem : ViewModelBase
        {
            public int ItemId { get; set; }
            public int GuestIndex { get; set; }
            public string DishName { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public decimal Price { get; set; }

            private string _status = "Ordered";
            public string Status
            {
                get => _status;
                set => Set(ref _status, value);
            }

            public string? Notes { get; set; }
        }

        public class WaiterNotificationItem : ViewModelBase
        {
            public int Id { get; set; }
            public int TableId { get; set; }
            public int? OrderId { get; set; }
            public string ZoneCode { get; set; } = string.Empty;
            public string TableName { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public DateTime CreatedAt { get; set; } = DateTime.Now;

            private bool _isRead;
            public bool IsRead
            {
                get => _isRead;
                set
                {
                    if (Set(ref _isRead, value))
                        Raise(nameof(IsRead));
                }
            }
        }

        #endregion
    }
}
