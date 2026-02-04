using System;
using System.Windows.Input;
using Autodesk.AutoCAD.DatabaseServices;

namespace Cadastre_Calculator
{
    public class ParcelViewModel : ViewModelBase
    {
        private readonly IAutoCADContext _context;
        private readonly IPersistenceService _persistenceService;
        private ObjectId _selectedEntityId;

        private string _owner = string.Empty;
        public string Owner
        {
            get => _owner;
            set => SetProperty(ref _owner, value);
        }

        private string _legalDesc = string.Empty;
        public string LegalDesc
        {
            get => _legalDesc;
            set => SetProperty(ref _legalDesc, value);
        }

        public ICommand SaveCommand { get; }

        public ParcelViewModel(IAutoCADContext context, IPersistenceService persistenceService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));

            // Initialize the command with the AutoCADRelayCommand to handle document locking
            SaveCommand = new AutoCADRelayCommand(_context, ExecuteSave, CanSave);
        }

        public void SetSelectedParcel(ObjectId entityId)
        {
            _selectedEntityId = entityId;
            // Load existing data if any
            LoadData();
        }

        private bool CanSave(object? parameter)
        {
            return !_selectedEntityId.IsNull && !string.IsNullOrWhiteSpace(Owner);
        }

        private void ExecuteSave(object? parameter)
        {
            if (_selectedEntityId.IsNull) return;

            using (var tr = _context.ActiveDocument.TransactionManager.StartTransaction())
            {
                var data = new ParcelData
                {
                    Owner = this.Owner,
                    LegalDesc = this.LegalDesc
                };

                _persistenceService.SaveData(tr, _selectedEntityId, "ParcelAttributes", data);
                tr.Commit();
            }
        }

        private void LoadData()
        {
            if (_selectedEntityId.IsNull) return;

            using (var tr = _context.ActiveDocument.TransactionManager.StartTransaction())
            {
                var data = _persistenceService.LoadData<ParcelData>(tr, _selectedEntityId, "ParcelAttributes");
                if (data != null)
                {
                    Owner = data.Owner;
                    LegalDesc = data.LegalDesc;
                }
                tr.Commit();
            }
        }
    }

    public class ParcelData
    {
        public string Owner { get; set; } = string.Empty;
        public string LegalDesc { get; set; } = string.Empty;
    }
}