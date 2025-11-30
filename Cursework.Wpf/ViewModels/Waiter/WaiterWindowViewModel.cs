// Project/Cursework.Wpf/ViewModels/Waiter/WaiterWindowViewModel.cs
using Cursework.Application.Interfaces;
using Cursework.Application.Realtime;
using Cursework.Domains.Models;
using Cursework.Wpf.Models.HallLayout;
using Cursework.Wpf.Services.HallLayout;
using Cursework.Wpf.Services.Realtime;
using Cursework.Wpf.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Cursework.Wpf.ViewModels.Waiter
{
    public class WaiterWindowViewModel : ViewModelBase
    {
        private readonly ITableService _tableService;
        private readonly IHallLayoutStorageService _layoutStorage;
        private readonly ICallWaiterService _callWaiterService;
        private readonly IRealtimeService _realtime;

        private readonly List<DiningTable> _allTables = new();

        // Порядок зон по кругу: VIP → Bar → MainHall → Terrace → VIP
        private static readonly string[] ZoneCycle = { "VIP", "Bar", "MainHall", "Terrace" };

        public WaiterWindowViewModel(
            ITableService tableService,
            IHallLayoutStorageService layoutStorage,
            ICallWaiterService callWaiterService,
            IRealtimeService realtime)
        {
            _tableService = tableService;
            _layoutStorage = layoutStorage;
            _callWaiterService = callWaiterService;
            _realtime = realtime;

            // стартовая зона — основной зал
            _selectedZone = "MainHall";

            NextZoneCommand = new RelayCommand(_ => SwitchZone(+1));
            PrevZoneCommand = new RelayCommand(_ => SwitchZone(-1));

            ToggleNotificationsCommand = new RelayCommand(_ => IsNotificationsOpen = !IsNotificationsOpen);

            OpenOrderCommand = new RelayCommand(_ => OpenOrder(), _ => HasSelectedTable);
            MarkItemsServedCommand = new RelayCommand(_ => MarkItemsServed(), _ => HasSelectedTable);
            CloseOrderCommand = new RelayCommand(_ => CloseOrder(), _ => HasSelectedTable);

            Notifications.CollectionChanged += OnNotificationsCollectionChanged;
            _realtime.CallWaiterChanged += OnCallWaiterChanged;
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
                    Raise(nameof(HasSelectedTable));
            }
        }

        public bool HasSelectedTable => SelectedTableOnMap != null;

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
                // для официанта достаточно просто пустой карты
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

        #region Notifications

        public ObservableCollection<WaiterNotificationItem> Notifications { get; } = new();

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
            Notifications.Clear();

            var list = await _callWaiterService.GetAllAsync();

            foreach (var cw in list.OrderByDescending(c => c.CreatedAt))
                Notifications.Add(ToNotification(cw));

            Raise(nameof(UnreadNotificationsCount));
        }

        #endregion

        #region Commands

        public ICommand NextZoneCommand { get; }
        public ICommand PrevZoneCommand { get; }

        public ICommand ToggleNotificationsCommand { get; }

        public ICommand OpenOrderCommand { get; }
        public ICommand MarkItemsServedCommand { get; }
        public ICommand CloseOrderCommand { get; }

        private void OpenOrder()
        {
            // TODO: интеграция с IOrderService / окном заказа
        }

        private void MarkItemsServed()
        {
            // TODO: вызвать API для изменения статусов позиций в заказе
        }

        private void CloseOrder()
        {
            // TODO: закрытие заказа через IOrderService
        }

        #endregion

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

        public class WaiterNotificationItem : ViewModelBase
        {
            public int Id { get; set; }
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

        #region Notifications helpers

        private WaiterNotificationItem ToNotification(CallWaiter callWaiter)
        {
            var tableName = _allTables.FirstOrDefault(t => t.Id == callWaiter.TableId)?.Name
                            ?? $"Стол #{callWaiter.TableId}";

            var title = callWaiter.Type switch
            {
                "Call" => "Вызов официанта",
                "AcceptPreorder" => "Принять предзаказ",
                _ => callWaiter.Type
            };

            return new WaiterNotificationItem
            {
                Id = callWaiter.Id,
                Title = $"{tableName} — {title}",
                Message = callWaiter.IsHandled ? "Обработано" : "Требует внимания",
                CreatedAt = callWaiter.CreatedAt,
                IsRead = callWaiter.IsHandled
            };
        }

        private void OnNotificationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<WaiterNotificationItem>())
                    item.PropertyChanged += NotificationPropertyChanged;
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<WaiterNotificationItem>())
                    item.PropertyChanged -= NotificationPropertyChanged;
            }

            Raise(nameof(UnreadNotificationsCount));
        }

        private void NotificationPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WaiterNotificationItem.IsRead))
                Raise(nameof(UnreadNotificationsCount));
        }

        private void OnCallWaiterChanged(CallWaiterChangedDto dto)
        {
            if (dto.CallWaiter == null)
                return;

            RunOnUi(() =>
            {
                var existing = Notifications.FirstOrDefault(n => n.Id == dto.CallWaiter.Id);

                switch (dto.Action)
                {
                    case EntityChangeAction.Created:
                        if (existing == null)
                            Notifications.Insert(0, ToNotification(dto.CallWaiter));
                        else
                        {
                            var idx = Notifications.IndexOf(existing);
                            Notifications[idx] = ToNotification(dto.CallWaiter);
                        }
                        break;

                    case EntityChangeAction.Updated:
                        if (existing != null)
                        {
                            var idx = Notifications.IndexOf(existing);
                            Notifications[idx] = ToNotification(dto.CallWaiter);
                        }
                        else
                        {
                            Notifications.Insert(0, ToNotification(dto.CallWaiter));
                        }
                        break;

                    case EntityChangeAction.Deleted:
                        if (existing != null)
                            Notifications.Remove(existing);
                        break;
                }

                Raise(nameof(UnreadNotificationsCount));
            });
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }

        #endregion
    }
}
