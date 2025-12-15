using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Core database service responsible for project creation, schema management, and project metadata.
    /// This service owns the complete database schema - all table creation happens here.
    /// </summary>
    public class ProjectDatabaseService
    {
        private readonly string _projectDbPath;

        public ProjectDatabaseService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        /// <summary>
        /// Creates a new project database with all necessary tables
        /// </summary>
        public async Task CreateProjectAsync(string projectName, string description)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await CreateProjectSchemaAsync(connection);

                // Insert initial project info
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO project_info (project_name, created_date, last_modified, description)
                        VALUES (@name, @created, @modified, @description)
                    ";
                    command.Parameters.AddWithValue("@name", projectName);
                    command.Parameters.AddWithValue("@created", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@modified", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@description", description ?? "");
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Creates the complete project database schema.
        /// ALL table definitions are centralized here for consistency and easier migrations.
        /// </summary>
        private async Task CreateProjectSchemaAsync(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
            -- ==================== PROJECT TABLES ====================
            
            -- Project metadata
            CREATE TABLE IF NOT EXISTS project_info (
                project_id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_name TEXT NOT NULL,
                created_date TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                description TEXT
            );

            -- Plates (technical metadata only - NO biological condition or batch)
            CREATE TABLE IF NOT EXISTS plates (
                plate_id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id INTEGER NOT NULL,
                plate_name TEXT NOT NULL,
                run_date TEXT,
                instrument_name TEXT,
                operator_name TEXT,
                description TEXT,
                FOREIGN KEY (project_id) REFERENCES project_info(project_id)
            );

            -- Parquet file imports
            CREATE TABLE IF NOT EXISTS parquet_imports (
                import_id INTEGER PRIMARY KEY AUTOINCREMENT,
                plate_id INTEGER NOT NULL,
                file_name TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                import_timestamp TEXT NOT NULL,
                row_count INTEGER NOT NULL,
                protein_count INTEGER NOT NULL,
                cell_count INTEGER NOT NULL,
                column_mapping TEXT NOT NULL,
                FOREIGN KEY (plate_id) REFERENCES plates(plate_id)
            );

            -- Raw files (biological condition stored at raw file level)
            CREATE TABLE IF NOT EXISTS raw_files (
                raw_file_id INTEGER PRIMARY KEY AUTOINCREMENT,
                import_id INTEGER NOT NULL,
                plate_id INTEGER NOT NULL,
                raw_file_name TEXT NOT NULL,
                biological_condition TEXT,
                protein_count INTEGER,
                peptide_count INTEGER,
                total_ion_current REAL,
                FOREIGN KEY (import_id) REFERENCES parquet_imports(import_id),
                FOREIGN KEY (plate_id) REFERENCES plates(plate_id)
            );

            -- Protein quantification summary
            CREATE TABLE IF NOT EXISTS protein_quant_summary (
                protein_id TEXT NOT NULL,
                raw_file_id INTEGER NOT NULL,
                median_intensity REAL,
                mean_intensity REAL,
                detection_count INTEGER,
                PRIMARY KEY (protein_id, raw_file_id),
                FOREIGN KEY (raw_file_id) REFERENCES raw_files(raw_file_id)
            );

            -- ==================== REFERENCE DATA TABLES ====================
            
            -- Cell type profiles (transcriptomic reference data)
            CREATE TABLE IF NOT EXISTS cell_type_profiles (
                cell_type TEXT NOT NULL,
                gene_name TEXT NOT NULL,
                median_expression REAL NOT NULL,
                mean_expression REAL NOT NULL,
                percent_expressing REAL NOT NULL,
                PRIMARY KEY (cell_type, gene_name)
            ) WITHOUT ROWID;

            -- Cell type metadata
            CREATE TABLE IF NOT EXISTS cell_type_metadata (
                cell_type TEXT PRIMARY KEY,
                cell_count INTEGER NOT NULL,
                genes_expressed INTEGER NOT NULL,
                age_range TEXT,
                batch_info TEXT
            );

            -- Gene to GO term annotations (for transcriptomic gene mappings)
            CREATE TABLE IF NOT EXISTS go_annotations (
                gene_name TEXT NOT NULL,
                go_id TEXT NOT NULL,
                PRIMARY KEY (gene_name, go_id),
                FOREIGN KEY (go_id) REFERENCES go_terms(go_id)
            );

            -- ==================== CELL TYPE CLASSIFICATIONS ====================
            
            -- Raw file cell type classifications
            CREATE TABLE IF NOT EXISTS raw_file_cell_type_classifications (
                classification_id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_file_id INTEGER NOT NULL UNIQUE,
                predicted_cell_type TEXT NOT NULL,
                composite_score REAL NOT NULL,
                spearman_correlation REAL NOT NULL,
                specificity_score REAL NOT NULL,
                hypergeometric_pvalue REAL NOT NULL,
                classified_at TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (raw_file_id) REFERENCES raw_files(raw_file_id)
            );

            -- ==================== INDICES ====================
            
            -- Project data indices
            CREATE INDEX IF NOT EXISTS idx_parquet_imports_plate ON parquet_imports(plate_id);
            CREATE INDEX IF NOT EXISTS idx_parquet_imports_filename ON parquet_imports(file_name);
            CREATE INDEX IF NOT EXISTS idx_raw_files_import ON raw_files(import_id);
            CREATE INDEX IF NOT EXISTS idx_raw_files_plate ON raw_files(plate_id);
            CREATE INDEX IF NOT EXISTS idx_raw_files_condition ON raw_files(biological_condition);
            CREATE INDEX IF NOT EXISTS idx_protein_quant_protein ON protein_quant_summary(protein_id);
            CREATE INDEX IF NOT EXISTS idx_protein_quant_rawfile ON protein_quant_summary(raw_file_id);
            
            -- Reference data indices
            CREATE INDEX IF NOT EXISTS idx_cell_type_profiles_cell_type ON cell_type_profiles(cell_type);
            CREATE INDEX IF NOT EXISTS idx_cell_type_profiles_gene ON cell_type_profiles(gene_name);
            CREATE INDEX IF NOT EXISTS idx_protein_go ON protein_go_annotations(protein_id);
            CREATE INDEX IF NOT EXISTS idx_go_protein ON protein_go_annotations(go_term_id);
        ";
                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Loads project information
        /// </summary>
        public async Task<ProjectInfo> GetProjectInfoAsync()
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM project_info LIMIT 1";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new ProjectInfo
                            {
                                ProjectId = reader.GetInt32(0),
                                ProjectName = reader.GetString(1),
                                CreatedDate = DateTime.Parse(reader.GetString(2)),
                                LastModified = DateTime.Parse(reader.GetString(3)),
                                Description = reader.IsDBNull(4) ? "" : reader.GetString(4)
                            };
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Ensures the cell type classifications table exists (for existing databases created before this feature)
        /// </summary>
        public async Task EnsureCellTypeClassificationsTableExistsAsync()
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                CREATE TABLE IF NOT EXISTS raw_file_cell_type_classifications (
                    classification_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    raw_file_id INTEGER NOT NULL UNIQUE,
                    predicted_cell_type TEXT NOT NULL,
                    composite_score REAL NOT NULL,
                    spearman_correlation REAL NOT NULL,
                    specificity_score REAL NOT NULL,
                    hypergeometric_pvalue REAL NOT NULL,
                    classified_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (raw_file_id) REFERENCES raw_files(raw_file_id)
                );
            ";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
}