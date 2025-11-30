// Project/Cursework.Wpf/ViewModels/Waiter/WaiterWindowViewModel.cs
using Cursework.Application.Interfaces;
using Cursework.Domains.Models;
using Cursework.Wpf.Models.HallLayout;
using Cursework.Wpf.Services.HallLayout;
using Cursework.Wpf.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Cursework.Wpf.ViewModels.Waiter
{
    public class WaiterWindowViewModel : ViewModelBase
    {
        private readonly ITableService _tableService;
        private readonly IHallLayoutStorageService _layoutStorage;

        private readonly List<DiningTable> _allTables = new();

        // Порядок зон по кругу: VIP → Bar → MainHall → Terrace → VIP
        private static readonly string[] ZoneCycle = { "VIP", "Bar", "MainHall", "Terrace" };

        public WaiterWindowViewModel(
            ITableService tableService,
            IHallLayoutStorageService layoutStorage)
        {
            _tableService = tableService;
            _layoutStorage = layoutStorage;

            // стартовая зона — основной зал
            _selectedZone = "MainHall";

            NextZoneCommand = new RelayCommand(_ => SwitchZone(+1));
            PrevZoneCommand = new RelayCommand(_ => SwitchZone(-1));

            ToggleNotificationsCommand = new RelayCommand(_ => IsNotificationsOpen = !IsNotificationsOpen);

            OpenOrderCommand = new RelayCommand(_ => OpenOrder(), _ => HasSelectedTable);
            MarkItemsServedCommand = new RelayCommand(_ => MarkItemsServed(), _ => HasSelectedTable);
            CloseOrderCommand = new RelayCommand(_ => CloseOrder(), _ => HasSelectedTable);
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
    }
}
