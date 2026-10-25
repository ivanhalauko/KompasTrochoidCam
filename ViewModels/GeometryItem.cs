using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KompasTrochoidCam.ViewModels
{
    /// <summary>Строка области обработки в правой панели (грань или ребро).</summary>
    public sealed partial class GeometryItem : ObservableObject
    {
        public int Id { get; }
        public string Title { get; }
        public string Details { get; }

        [ObservableProperty]
        private bool _isSelected;

        public GeometryItem(int id, string title, string details, bool isSelected)
        {
            Id = id;
            Title = title;
            Details = details;
            _isSelected = isSelected;
        }
    }
}
