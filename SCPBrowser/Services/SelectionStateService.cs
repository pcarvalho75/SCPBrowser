using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Centralized service for managing selection state across the application.
    /// Handles cell type filters, biological condition filters, lasso selection,
    /// and individual run exclusions.
    /// </summary>
    public class SelectionStateService
    {
        // Selection state
        private HashSet<string> _checkedCellTypes = new HashSet<string>();
        private HashSet<string> _checkedBioConditions = new HashSet<string>();
        private HashSet<string> _excludedRunNames = new HashSet<string>();
        private HashSet<string> _lassoSelectedRuns = new HashSet<string>();
        private bool _isLassoActive = false;

        // Available options (for "check all" defaults)
        private HashSet<string> _availableCellTypes = new HashSet<string>();
        private HashSet<string> _availableBioConditions = new HashSet<string>();

        // Event fired when selection changes
        public event EventHandler SelectionChanged;

        // Properties
        public HashSet<string> CheckedCellTypes => _checkedCellTypes;
        public HashSet<string> CheckedBioConditions => _checkedBioConditions;
        public HashSet<string> ExcludedRunNames => _excludedRunNames;
        public bool IsLassoActive => _isLassoActive;
        public HashSet<string> LassoSelectedRuns => _lassoSelectedRuns;

        /// <summary>
        /// Initializes available cell types (from predictions) and checks all by default
        /// </summary>
        public void SetAvailableCellTypes(IEnumerable<string> cellTypes, bool checkAll = true)
        {
            _availableCellTypes = new HashSet<string>(cellTypes.Where(c => !string.IsNullOrEmpty(c)));

            if (checkAll)
            {
                _checkedCellTypes = new HashSet<string>(_availableCellTypes);
            }
        }

        /// <summary>
        /// Initializes available biological conditions and checks all by default
        /// </summary>
        public void SetAvailableBioConditions(IEnumerable<string> conditions, bool checkAll = true)
        {
            _availableBioConditions = new HashSet<string>(conditions.Where(c => !string.IsNullOrEmpty(c)));

            if (checkAll)
            {
                _checkedBioConditions = new HashSet<string>(_availableBioConditions);
            }
        }

        /// <summary>
        /// Sets the excluded run names (loaded from database)
        /// </summary>
        public void SetExcludedRuns(IEnumerable<string> excludedRunNames)
        {
            _excludedRunNames = new HashSet<string>(excludedRunNames);
        }

        /// <summary>
        /// Updates cell type selection
        /// </summary>
        public void SetCellTypeChecked(string cellType, bool isChecked)
        {
            if (isChecked)
                _checkedCellTypes.Add(cellType);
            else
                _checkedCellTypes.Remove(cellType);

            RaiseSelectionChanged();
        }

        /// <summary>
        /// Updates biological condition selection
        /// </summary>
        public void SetBioConditionChecked(string condition, bool isChecked)
        {
            if (isChecked)
                _checkedBioConditions.Add(condition);
            else
                _checkedBioConditions.Remove(condition);

            RaiseSelectionChanged();
        }

        /// <summary>
        /// Sets lasso selection state
        /// </summary>
        public void SetLassoSelection(IEnumerable<string> selectedRuns)
        {
            if (selectedRuns == null || !selectedRuns.Any())
            {
                _isLassoActive = false;
                _lassoSelectedRuns.Clear();
            }
            else
            {
                _isLassoActive = true;
                _lassoSelectedRuns = new HashSet<string>(selectedRuns);
            }

            RaiseSelectionChanged();
        }

        /// <summary>
        /// Clears lasso selection
        /// </summary>
        public void ClearLassoSelection()
        {
            _isLassoActive = false;
            _lassoSelectedRuns.Clear();
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Excludes a run by name
        /// </summary>
        public void ExcludeRun(string runName)
        {
            _excludedRunNames.Add(runName);
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Includes a previously excluded run
        /// </summary>
        public void IncludeRun(string runName)
        {
            _excludedRunNames.Remove(runName);
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Clears all exclusions
        /// </summary>
        public void ClearAllExclusions()
        {
            _excludedRunNames.Clear();
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Checks all cell types
        /// </summary>
        public void CheckAllCellTypes()
        {
            _checkedCellTypes = new HashSet<string>(_availableCellTypes);
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Checks all biological conditions
        /// </summary>
        public void CheckAllBioConditions()
        {
            _checkedBioConditions = new HashSet<string>(_availableBioConditions);
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Gets the effective selected runs based on current selection state.
        /// 
        /// Logic:
        /// IF lasso is active:
        ///     Selected = (Lasso Runs) ∩ (Checked Bio Conditions) − (Excluded Runs)
        /// ELSE:
        ///     Selected = (Checked Cell Types) ∩ (Checked Bio Conditions) − (Excluded Runs)
        /// </summary>
        public List<string> GetEffectiveSelectedRuns(
            ProteomicsData data,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions)
        {
            if (data == null || data.RawFileNames == null)
                return new List<string>();

            var selectedRuns = new List<string>();

            foreach (var runName in data.RawFileNames)
            {
                // Skip excluded runs
                if (_excludedRunNames.Contains(runName))
                    continue;

                // Check biological condition filter
                bool matchesBioCondition = true;
                if (_checkedBioConditions.Count > 0 && _availableBioConditions.Count > 0)
                {
                    string bioCondition = null;
                    if (data.BiologicalConditionPerFile != null)
                        data.BiologicalConditionPerFile.TryGetValue(runName, out bioCondition);

                    matchesBioCondition = !string.IsNullOrEmpty(bioCondition) &&
                                          _checkedBioConditions.Contains(bioCondition);
                }

                if (!matchesBioCondition)
                    continue;

                // Check cell type or lasso filter
                if (_isLassoActive)
                {
                    // Lasso mode: cell types ignored, only check if run is in lasso
                    if (_lassoSelectedRuns.Contains(runName))
                    {
                        selectedRuns.Add(runName);
                    }
                }
                else
                {
                    // Normal mode: check cell type filter
                    bool matchesCellType = true;
                    if (_checkedCellTypes.Count > 0 && _availableCellTypes.Count > 0 && cellTypePredictions != null)
                    {
                        string cellType = null;
                        if (cellTypePredictions.TryGetValue(runName, out var prediction))
                            cellType = prediction.TopCellType;

                        matchesCellType = !string.IsNullOrEmpty(cellType) &&
                                          _checkedCellTypes.Contains(cellType);
                    }

                    if (matchesCellType)
                    {
                        selectedRuns.Add(runName);
                    }
                }
            }

            return selectedRuns;
        }

        /// <summary>
        /// Generates a human-readable selection rule text
        /// </summary>
        public string GetSelectionRuleText(int effectiveRunCount)
        {
            if (_isLassoActive)
            {
                // Lasso mode
                string bioConditionPart = GetBioConditionRulePart();
                string lassoCount = _lassoSelectedRuns.Count.ToString();

                if (string.IsNullOrEmpty(bioConditionPart))
                {
                    return $"📋 Lasso ({lassoCount} runs) → {effectiveRunCount} runs  [Cell type filter disabled]";
                }
                else
                {
                    return $"📋 Lasso ({lassoCount} runs) ∩ {bioConditionPart} → {effectiveRunCount} runs  [Cell type filter disabled]";
                }
            }
            else
            {
                // Normal mode
                string cellTypePart = GetCellTypeRulePart();
                string bioConditionPart = GetBioConditionRulePart();

                if (string.IsNullOrEmpty(cellTypePart) && string.IsNullOrEmpty(bioConditionPart))
                {
                    return $"📋 All runs included (no filter) → {effectiveRunCount} runs";
                }
                else if (string.IsNullOrEmpty(bioConditionPart))
                {
                    return $"📋 {cellTypePart} → {effectiveRunCount} runs";
                }
                else if (string.IsNullOrEmpty(cellTypePart))
                {
                    return $"📋 {bioConditionPart} → {effectiveRunCount} runs";
                }
                else
                {
                    return $"📋 {cellTypePart} ∩ {bioConditionPart} → {effectiveRunCount} runs";
                }
            }
        }

        private string GetCellTypeRulePart()
        {
            if (_checkedCellTypes.Count == 0 || _availableCellTypes.Count == 0)
                return null;

            if (_checkedCellTypes.Count == _availableCellTypes.Count)
                return "All Cell Types";

            var sorted = _checkedCellTypes.OrderBy(c => c).ToList();
            if (sorted.Count <= 3)
            {
                return "(" + string.Join(" ∪ ", sorted) + ")";
            }
            else
            {
                return $"({sorted.Count} cell types)";
            }
        }

        private string GetBioConditionRulePart()
        {
            if (_checkedBioConditions.Count == 0 || _availableBioConditions.Count == 0)
                return null;

            if (_checkedBioConditions.Count == _availableBioConditions.Count)
                return "All Conditions";

            var sorted = _checkedBioConditions.OrderBy(c => c).ToList();
            if (sorted.Count <= 3)
            {
                return "(" + string.Join(" ∪ ", sorted) + ")";
            }
            else
            {
                return $"({sorted.Count} conditions)";
            }
        }

        private void RaiseSelectionChanged()
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Resets all selection state
        /// </summary>
        public void Reset()
        {
            _checkedCellTypes.Clear();
            _checkedBioConditions.Clear();
            _excludedRunNames.Clear();
            _lassoSelectedRuns.Clear();
            _isLassoActive = false;
            _availableCellTypes.Clear();
            _availableBioConditions.Clear();
        }
    }
}