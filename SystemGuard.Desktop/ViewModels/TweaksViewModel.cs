using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.ViewModels;

public partial class CategoryChip : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;

    public CategoryChip(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }
}

public partial class TweakRowViewModel : ObservableObject
{
    public TweakDefinition Definition { get; }
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _statusText = "";

    public string ToggleLabel => IsBusy ? "…" : IsConfirming ? "Confirm?" : (IsOn ? "On" : "Off");

    public TweakRowViewModel(TweakDefinition definition, bool isOn)
    {
        Definition = definition;
        _isOn = isOn;
    }

    partial void OnIsConfirmingChanged(bool value) => OnPropertyChanged(nameof(ToggleLabel));
    partial void OnIsOnChanged(bool value) => OnPropertyChanged(nameof(ToggleLabel));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ToggleLabel));
}

public partial class TweaksViewModel : ViewModelBase
{
    private readonly ITweaksService _service;
    private List<TweakRowViewModel> _allRows = new();

    [ObservableProperty] private ObservableCollection<TweakRowViewModel> _filteredRows = new();
    [ObservableProperty] private ObservableCollection<CategoryChip> _categories = new();
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private int _implementedCount;
    [ObservableProperty] private int _totalCount;

    public IRelayCommand<string> SelectCategoryCommand { get; }
    public IAsyncRelayCommand<TweakRowViewModel> ToggleTweakCommand { get; }

    public TweaksViewModel(ITweaksService service)
    {
        _service = service;
        SelectCategoryCommand = new RelayCommand<string>(SelectCategory);
        ToggleTweakCommand = new AsyncRelayCommand<TweakRowViewModel>(ToggleAsync);
        Load();
    }

    private void Load()
    {
        var defs = _service.GetAllTweaks();
        TotalCount = defs.Count;
        ImplementedCount = defs.Count(d => d.IsImplemented);

        _allRows = defs.Select(d => new TweakRowViewModel(d, d.IsImplemented && _service.IsApplied(d.Id))).ToList();

        var cats = new List<string> { "All" };
        cats.AddRange(defs.Select(d => d.Category.ToString()).Distinct().OrderBy(c => c));
        Categories = new ObservableCollection<CategoryChip>(cats.Select(c => new CategoryChip(c, c == "All")));

        ApplyFilter();
    }

    private void SelectCategory(string? category)
    {
        if (category == null) return;
        SelectedCategory = category;
        foreach (var c in Categories) c.IsSelected = c.Name == category;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<TweakRowViewModel> q = _allRows;
        if (SelectedCategory != "All")
            q = q.Where(r => r.Definition.Category.ToString() == SelectedCategory);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.ToLowerInvariant();
            q = q.Where(r => r.Definition.Name.ToLowerInvariant().Contains(s) || r.Definition.Description.ToLowerInvariant().Contains(s));
        }
        FilteredRows = new ObservableCollection<TweakRowViewModel>(q);
    }

    private async Task ToggleAsync(TweakRowViewModel? row)
    {
        if (row == null || !row.Definition.IsImplemented || row.IsBusy) return;

        if (row.Definition.IsDangerous && !row.IsConfirming)
        {
            row.IsConfirming = true;
            row.StatusText = "Tap again to confirm";
            _ = ResetConfirmAsync(row);
            return;
        }
        row.IsConfirming = false;

        row.IsBusy = true;
        var result = row.IsOn
            ? await Task.Run(() => _service.Revert(row.Definition.Id))
            : await Task.Run(() => _service.Apply(row.Definition.Id));
        row.IsBusy = false;

        row.StatusText = result.Message;
        if (result.Success) row.IsOn = !row.IsOn;
    }

    private async Task ResetConfirmAsync(TweakRowViewModel row)
    {
        await Task.Delay(4000);
        if (row.IsConfirming) { row.IsConfirming = false; row.StatusText = ""; }
    }
}