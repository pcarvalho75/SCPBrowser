using System;
using System.ComponentModel;

namespace SCPBrowser.Models
{
    /// <summary>
    /// Represents a single plate/experiment in the project
    /// </summary>
    public class PlateInfo : INotifyPropertyChanged
    {
        private int _plateId;
        private string _plateName;
        private string _runDate;
        private string _biologicalCondition;
        private string _description;
        private string _instrumentName;
        private string _operatorName;
        private string _batchNumber;
        private int _fileCount;

        public int PlateId
        {
            get => _plateId;
            set
            {
                if (_plateId != value)
                {
                    _plateId = value;
                    OnPropertyChanged(nameof(PlateId));
                }
            }
        }

        public string PlateName
        {
            get => _plateName;
            set
            {
                if (_plateName != value)
                {
                    _plateName = value;
                    OnPropertyChanged(nameof(PlateName));
                }
            }
        }

        public string RunDate
        {
            get => _runDate;
            set
            {
                if (_runDate != value)
                {
                    _runDate = value;
                    OnPropertyChanged(nameof(RunDate));
                }
            }
        }

        public string BiologicalCondition
        {
            get => _biologicalCondition;
            set
            {
                if (_biologicalCondition != value)
                {
                    _biologicalCondition = value;
                    OnPropertyChanged(nameof(BiologicalCondition));
                }
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value;
                    OnPropertyChanged(nameof(Description));
                }
            }
        }

        public string InstrumentName
        {
            get => _instrumentName;
            set
            {
                if (_instrumentName != value)
                {
                    _instrumentName = value;
                    OnPropertyChanged(nameof(InstrumentName));
                }
            }
        }

        public string OperatorName
        {
            get => _operatorName;
            set
            {
                if (_operatorName != value)
                {
                    _operatorName = value;
                    OnPropertyChanged(nameof(OperatorName));
                }
            }
        }

        public string BatchNumber
        {
            get => _batchNumber;
            set
            {
                if (_batchNumber != value)
                {
                    _batchNumber = value;
                    OnPropertyChanged(nameof(BatchNumber));
                }
            }
        }

        public int FileCount
        {
            get => _fileCount;
            set
            {
                if (_fileCount != value)
                {
                    _fileCount = value;
                    OnPropertyChanged(nameof(FileCount));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents a single raw file run within an imported parquet
    /// </summary>
    public class RawFileInfo : INotifyPropertyChanged
    {
        private int _rawFileId;
        private int _importId;
        private string _rawFileName;
        private string _biologicalCondition;
        private int? _plateId;
        private string _plateName;
        private int _proteinCount;
        private int _peptideCount;
        private double _totalIonCurrent;

        public int RawFileId
        {
            get => _rawFileId;
            set
            {
                if (_rawFileId != value)
                {
                    _rawFileId = value;
                    OnPropertyChanged(nameof(RawFileId));
                }
            }
        }

        public int ImportId
        {
            get => _importId;
            set
            {
                if (_importId != value)
                {
                    _importId = value;
                    OnPropertyChanged(nameof(ImportId));
                }
            }
        }

        public string RawFileName
        {
            get => _rawFileName;
            set
            {
                if (_rawFileName != value)
                {
                    _rawFileName = value;
                    OnPropertyChanged(nameof(RawFileName));
                }
            }
        }

        public string BiologicalCondition
        {
            get => _biologicalCondition;
            set
            {
                if (_biologicalCondition != value)
                {
                    _biologicalCondition = value;
                    OnPropertyChanged(nameof(BiologicalCondition));
                }
            }
        }

        public int? PlateId
        {
            get => _plateId;
            set
            {
                if (_plateId != value)
                {
                    _plateId = value;
                    OnPropertyChanged(nameof(PlateId));
                }
            }
        }

        public string PlateName
        {
            get => _plateName;
            set
            {
                if (_plateName != value)
                {
                    _plateName = value;
                    OnPropertyChanged(nameof(PlateName));
                }
            }
        }

        public int ProteinCount
        {
            get => _proteinCount;
            set
            {
                if (_proteinCount != value)
                {
                    _proteinCount = value;
                    OnPropertyChanged(nameof(ProteinCount));
                }
            }
        }

        public int PeptideCount
        {
            get => _peptideCount;
            set
            {
                if (_peptideCount != value)
                {
                    _peptideCount = value;
                    OnPropertyChanged(nameof(PeptideCount));
                }
            }
        }

        public double TotalIonCurrent
        {
            get => _totalIonCurrent;
            set
            {
                if (_totalIonCurrent != value)
                {
                    _totalIonCurrent = value;
                    OnPropertyChanged(nameof(TotalIonCurrent));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents a parquet file import record
    /// </summary>
    public class ParquetImportInfo
    {
        public int ImportId { get; set; }
        public int PlateId { get; set; }
        public string FileName { get; set; }
        public string FileHash { get; set; }
        public DateTime ImportTimestamp { get; set; }
        public int RowCount { get; set; }
        public int ProteinCount { get; set; }
        public int CellCount { get; set; }
        public string ColumnMappingJson { get; set; }
    }

    /// <summary>
    /// Project-level information
    /// </summary>
    public class ProjectInfo
    {
        public int ProjectId { get; set; }
        public string ProjectName { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public string Description { get; set; }
    }
}