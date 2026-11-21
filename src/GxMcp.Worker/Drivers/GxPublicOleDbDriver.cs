using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Drivers
{
    public interface IGxPublicConnection : IDisposable
    {
        string ProviderName { get; }
        bool IsOpen { get; }
        void Open(string kbPath);
        void Close();
        IDataReader ExecuteReader(string sql);
    }

    public sealed class GxPublicObjectMetadata
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string ModelId { get; set; }
        public string ModelName { get; set; }
        public string ClassGuid { get; set; }
        public string LastUpdate { get; set; }
    }

    /// <summary>
    /// Read-only GXPublic OLE DB access for classic GeneXus KBs.
    /// GXPublic publishes metadata tables; it is not the modern SDK and does
    /// not provide the native source/edit contract.
    /// </summary>
    public sealed class GxPublicOleDbDriver : IDisposable
    {
        private static readonly string[] CandidateProgIds =
        {
            // GXPublic 8.0 is documented as provider .4; Yi/GeneXus 9 uses .5.
            "GXPublic.GXPublic.4",
            "GXPublic.GXPublic.5",
            // Some installations register the later provider under this alias.
            "GXPubGXX.GXPublic.5",
            "GXPubGXX.GXPublic"
        };

        private static readonly Dictionary<string, string> ObjectClassNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = "transaction",
                ["1"] = "procedure",
                ["2"] = "report",
                ["3"] = "menu",
                ["4"] = "work panel",
                ["5"] = "attribute",
                ["6"] = "data view",
                ["7"] = "table",
                ["8"] = "folder",
                ["9"] = "model",
                ["10"] = "group",
                ["11"] = "domain",
                ["12"] = "prompt",
                ["13"] = "web panel",
                ["14"] = "external program",
                ["25"] = "theme",
                ["26"] = "structured data type"
            };

        private readonly Func<string, Type> _progIdResolver;
        private readonly Func<string, IGxPublicConnection> _connectionFactory;
        private readonly List<string> _registeredProviders = new List<string>();
        private IGxPublicConnection _connection;
        private string _activeKbPath;
        private bool _isConnected;

        public GxPublicOleDbDriver(
            Func<string, Type> progIdResolver = null,
            Func<string, IGxPublicConnection> connectionFactory = null)
        {
            _progIdResolver = progIdResolver ?? (progId => Type.GetTypeFromProgID(progId));
            _connectionFactory = connectionFactory ?? (provider => new OleDbGxPublicConnection(provider));
            ResolveProviders();
        }

        public bool IsRegistered => _registeredProviders.Count > 0;
        public string ResolvedProgId { get; private set; }
        public bool IsConnected => _isConnected;
        public string ActiveKbPath => _activeKbPath;
        public IReadOnlyList<string> RegisteredProviders => _registeredProviders.AsReadOnly();

        private void ResolveProviders()
        {
            string targetMajor = Environment.GetEnvironmentVariable("GXMCP_TARGET_MAJOR");
            string preferred = Environment.GetEnvironmentVariable("GXMCP_GXPUBLIC_PROVIDER");
            IEnumerable<string> candidates = CandidateProgIds;
            if (string.Equals(targetMajor, "8", StringComparison.OrdinalIgnoreCase))
            {
                candidates = new[] { "GXPublic.GXPublic.4", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic" };
            }
            else if (string.Equals(targetMajor, "9", StringComparison.OrdinalIgnoreCase))
            {
                candidates = new[] { "GXPublic.GXPublic.5", "GXPubGXX.GXPublic.5", "GXPubGXX.GXPublic" };
            }
            if (!string.IsNullOrWhiteSpace(preferred))
                candidates = new[] { preferred }.Concat(candidates.Where(candidate => !string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase)));

            foreach (string progId in candidates)
            {
                try
                {
                    if (_progIdResolver(progId) != null)
                    {
                        _registeredProviders.Add(progId);
                        if (ResolvedProgId == null) ResolvedProgId = progId;
                    }
                }
                catch
                {
                    // A missing 32-bit provider is expected on many machines.
                }
            }
        }

        public bool OpenKB(string kbPath, out string errorJson)
        {
            errorJson = null;
            if (!IsRegistered)
            {
                errorJson = McpResponse.Err(
                    code: "GXMCP_GXPUBLIC_PROVIDER_NOT_REGISTERED",
                    message: "No supported GXPublic OLE DB provider is registered for the 32-bit Worker.",
                    hint: "Install the GXPublic provider matching the KB generation (GXPublic 8.0 for GX8 or GXPublic Yi for GX9); the GeneXus IDE alone does not prove that the provider is installed.",
                    target: kbPath);
                return false;
            }

            CloseKB();
            string lastError = null;
            foreach (string provider in _registeredProviders)
            {
                IGxPublicConnection candidate = null;
                try
                {
                    candidate = _connectionFactory(provider);
                    if (candidate == null) throw new InvalidOperationException("The GXPublic connection factory returned null.");

                    candidate.Open(kbPath);
                    _connection = candidate;
                    _activeKbPath = kbPath;
                    _isConnected = true;
                    ResolvedProgId = provider;
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = UnwrapMessage(ex);
                    try { candidate?.Dispose(); } catch { }
                }
            }

            errorJson = McpResponse.Err(
                code: "GXMCP_GXPUBLIC_OPEN_FAILED",
                message: "GXPublic could not open the Knowledge Base: " + (lastError ?? "unknown provider error"),
                hint: "Use the provider matching the KB generation. GXPublic Factory/provider selection must not convert an older KB implicitly; keep the GeneXus IDE closed while probing.",
                target: kbPath);
            return false;
        }

        public void CloseKB()
        {
            if (_connection != null)
            {
                try { _connection.Close(); } catch { }
                try { _connection.Dispose(); } catch { }
                _connection = null;
            }

            _isConnected = false;
            _activeKbPath = null;
        }

        public List<string> QueryObjects(string typeFilter, string nameFilter, out string error, bool exactMatch = false)
        {
            return QueryObjectMetadata(typeFilter, nameFilter, out error, exactMatch)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<GxPublicObjectMetadata> QueryObjectMetadata(string typeFilter, string nameFilter, out string error, bool exactMatch = false)
        {
            error = null;
            var list = new List<GxPublicObjectMetadata>();
            if (!EnsureConnected(out error)) return list;

            string stage = "ExecuteReader";
            try
            {
                using (IDataReader reader = _connection.ExecuteReader("SELECT * FROM Object"))
                {
                    stage = "FindOrdinal";
                    int nameOrdinal = FindOrdinal(reader, "ObjNam", "ObjName", "Name");
                    int classOrdinal = FindOrdinal(reader, "ObjClsName", "ObjCls", "ObjectClass");
                    if (nameOrdinal < 0)
                    {
                        error = "GXPublic Object metadata does not expose an object-name column (expected ObjNam).";
                        return list;
                    }

                    int idOrdinal = FindOrdinal(reader, "ObjId", "ObjectId", "Id");
                    int descriptionOrdinal = FindOrdinal(reader, "ObjDsc", "ObjDesc", "Description");
                    int modelIdOrdinal = FindOrdinal(reader, "MdlId", "ModelId");
                    int modelNameOrdinal = FindOrdinal(reader, "MdlNam", "ModelName");
                    int classGuidOrdinal = FindOrdinal(reader, "ObjClsGUID", "ObjClsGuid", "ClassGuid");
                    int lastUpdateOrdinal = FindOrdinal(reader, "ObjUpdTS", "ObjUpdateTS", "LastUpdate");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int rowNumber = 0;

                    while (true)
                    {
                        stage = "Read row " + (rowNumber + 1);
                        if (!reader.Read()) break;
                        rowNumber++;
                        stage = "GetValue row " + rowNumber;
                        string name = ReadValue(reader, nameOrdinal);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string rawClass = classOrdinal >= 0 ? ReadValue(reader, classOrdinal) : null;
                        string objectType = NormalizeObjectClass(rawClass);
                        if (!MatchesFilter(name, nameFilter, exactMatch) || !MatchesType(objectType, typeFilter)) continue;
                        if (!seen.Add(name)) continue;

                        list.Add(new GxPublicObjectMetadata
                        {
                            Id = ReadValue(reader, idOrdinal),
                            Name = name,
                            Type = objectType,
                            Description = ReadValue(reader, descriptionOrdinal),
                            ModelId = ReadValue(reader, modelIdOrdinal),
                            ModelName = ReadValue(reader, modelNameOrdinal),
                            ClassGuid = ReadValue(reader, classGuidOrdinal),
                            LastUpdate = ReadValue(reader, lastUpdateOrdinal)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                error = UnwrapMessage(ex) + " [GXPublic list failed at stage '" + stage + "', provider "
                    + (_connection?.ProviderName ?? "unknown") + ", rows read " + list.Count + "]";
            }

            return list;
        }

        public string ReadObjectPart(string objectName, string partName, out string error)
        {
            error = "GXPUBLIC_SOURCE_UNSUPPORTED: GXPublic publishes KB metadata tables, not the native MCP source-part contract.";
            return null;
        }

        public bool WriteObject(string objectName, string content, out string error)
        {
            error = "GXPUBLIC_MUTATION_UNSUPPORTED: GXPublic is a metadata provider; source/object writes require the matching GeneXus IDE or a documented legacy import surface.";
            return false;
        }

        public bool ExportXPZ(string xpzPath, IEnumerable<string> objectNames, out string error)
        {
            error = "GXPUBLIC_TRANSFER_UNSUPPORTED: GXPublic metadata access does not prove XPZ export support for this KB generation.";
            return false;
        }

        public bool ImportXPZ(string xpzPath, out string error)
        {
            error = "GXPUBLIC_TRANSFER_UNSUPPORTED: GXPublic metadata access does not prove XPZ import support for this KB generation.";
            return false;
        }

        private bool EnsureConnected(out string error)
        {
            if (_isConnected && _connection != null && _connection.IsOpen)
            {
                error = null;
                return true;
            }

            error = "KB is not connected under the GXPublic OLE DB driver.";
            return false;
        }

        private static int FindOrdinal(IDataReader reader, params string[] names)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string actual = reader.GetName(i);
                if (names.Any(name => string.Equals(name, actual, StringComparison.OrdinalIgnoreCase))) return i;
            }
            return -1;
        }

        private static string ReadValue(IDataReader reader, int ordinal)
        {
            if (ordinal < 0 || reader.IsDBNull(ordinal)) return null;
            object value = reader.GetValue(ordinal);
            if (value is DateTime timestamp)
                return timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool MatchesFilter(string value, string filter, bool exactMatch)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;

            string normalized = filter.Trim();
            if (normalized.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("name:".Length).Trim();
            bool quoted = normalized.Length >= 2 && normalized[0] == '"' && normalized[normalized.Length - 1] == '"';
            if (quoted) normalized = normalized.Substring(1, normalized.Length - 2);
            if (string.IsNullOrWhiteSpace(normalized)) return true;
            return exactMatch || quoted
                ? string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)
                : value.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesType(string value, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string normalizedFilter = filter.Trim();
            if (value.IndexOf(normalizedFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (ObjectClassNames.TryGetValue(normalizedFilter, out string filteredClass)
                && string.Equals(value, filteredClass, StringComparison.OrdinalIgnoreCase)) return true;
            return ObjectClassNames.TryGetValue(value.Trim(), out string className)
                && className.IndexOf(normalizedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeObjectClass(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            return ObjectClassNames.TryGetValue(trimmed, out string className) ? className : trimmed;
        }

        private static string UnwrapMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public void Dispose()
        {
            CloseKB();
        }
    }

    internal sealed class OleDbGxPublicConnection : IGxPublicConnection
    {
        private readonly string _providerName;
        private OleDbConnection _connection;
        private readonly List<OleDbCommand> _commands = new List<OleDbCommand>();

        public OleDbGxPublicConnection(string providerName)
        {
            _providerName = providerName;
        }

        public string ProviderName => _providerName;
        public bool IsOpen => _connection != null && _connection.State == ConnectionState.Open;

        public void Open(string kbPath)
        {
            Close();
            var builder = new OleDbConnectionStringBuilder();
            builder["Provider"] = _providerName;
            builder["Data Source"] = kbPath;
            _connection = new OleDbConnection(builder.ConnectionString);
            _connection.Open();
        }

        public IDataReader ExecuteReader(string sql)
        {
            if (!IsOpen) throw new InvalidOperationException("GXPublic connection is not open.");
            var command = _connection.CreateCommand();
            command.CommandText = sql;
            _commands.Add(command);
            return command.ExecuteReader();
        }

        public void Close()
        {
            foreach (OleDbCommand command in _commands)
            {
                try { command.Dispose(); } catch { }
            }
            _commands.Clear();
            if (_connection != null)
            {
                try { _connection.Close(); } catch { }
                try { _connection.Dispose(); } catch { }
                _connection = null;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
