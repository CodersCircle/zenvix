using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Modules.ApiTesting;

namespace Zenvix.ViewModels
{
    /// <summary>One editable row in the Params / Headers / Form / Variables tables.</summary>
    public partial class KvRow : ObservableObject
    {
        [ObservableProperty] private bool _enabled = true;
        [ObservableProperty] private string _key = "";
        [ObservableProperty] private string _value = "";

        public KvRow() { }
        public KvRow(KeyValue kv) { _enabled = kv.Enabled; _key = kv.Key; _value = kv.Value; }
        public KeyValue ToModel() => new() { Enabled = Enabled, Key = Key ?? "", Value = Value ?? "" };
    }

    public partial class ApiCollectionItem : ObservableObject
    {
        public ApiCollection Model { get; }
        public ObservableCollection<ApiRequest> Requests { get; }
        [ObservableProperty] private string _name;

        public ApiCollectionItem(ApiCollection model)
        {
            Model = model;
            _name = model.Name;
            Requests = new ObservableCollection<ApiRequest>(model.Requests);
        }

        partial void OnNameChanged(string value) => Model.Name = value;
    }

    public sealed class ApiHistoryItem
    {
        public ApiHistoryEntry Model { get; }
        public ApiHistoryItem(ApiHistoryEntry m) => Model = m;
        public string Method => Model.Request.Method;
        public string Url => Model.Request.Url;
        public string Info => $"{(Model.Status == 0 ? "ERR" : Model.Status.ToString())} · {Model.Milliseconds} ms · {Model.Time:HH:mm:ss}";
    }

    /// <summary>API Testing: a Postman-style request runner for locally hosted (and any) APIs.</summary>
    public partial class ApiTestingViewModel : ObservableObject
    {
        private readonly IApiTestingService _api;
        private CancellationTokenSource? _cts;
        private Guid? _editingId;

        public string[] Methods { get; } = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
        public BodyKind[] BodyKinds { get; } = Enum.GetValues<BodyKind>();
        public AuthKind[] AuthKinds { get; } = Enum.GetValues<AuthKind>();

        public ObservableCollection<ApiCollectionItem> Collections { get; } = new();
        public ObservableCollection<ApiHistoryItem> History { get; } = new();
        public ObservableCollection<ApiEnvironment> Environments { get; } = new();
        public ObservableCollection<KvRow> EnvVariables { get; } = new();

        // ── request editor ──
        public ObservableCollection<KvRow> QueryRows { get; } = new();
        public ObservableCollection<KvRow> HeaderRows { get; } = new();
        public ObservableCollection<KvRow> FormRows { get; } = new();

        [ObservableProperty] private string _requestName = "New request";
        [ObservableProperty] private string _method = "GET";
        [ObservableProperty] private string _url = "{{baseUrl}}/";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowRawBody), nameof(ShowFormBody))]
        private BodyKind _bodyKind = BodyKind.None;
        [ObservableProperty] private string _body = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBearer), nameof(IsApiKey), nameof(IsBasic))]
        private AuthKind _auth = AuthKind.None;
        [ObservableProperty] private string _token = "";
        [ObservableProperty] private string _apiKeyName = "X-API-Key";
        [ObservableProperty] private string _apiKeyValue = "";
        [ObservableProperty] private bool _apiKeyInQuery;
        [ObservableProperty] private string _basicUser = "";
        [ObservableProperty] private string _basicPassword = "";
        [ObservableProperty] private ApiCollectionItem? _selectedCollection;
        [ObservableProperty] private ApiEnvironment? _selectedEnvironment;
        [ObservableProperty] private bool _isEnvironmentEditorOpen;

        public bool ShowRawBody => BodyKind is BodyKind.Json or BodyKind.Text;
        public bool ShowFormBody => BodyKind is BodyKind.FormData or BodyKind.UrlEncoded;
        public bool IsBearer => Auth == AuthKind.Bearer;
        public bool IsApiKey => Auth == AuthKind.ApiKey;
        public bool IsBasic => Auth == AuthKind.Basic;

        // ── response ──
        [ObservableProperty] private bool _isSending;
        [ObservableProperty] private bool _hasResponse;
        [ObservableProperty] private string _statusText = "";
        [ObservableProperty] private bool _isSuccess;
        [ObservableProperty] private string _timeText = "";
        [ObservableProperty] private string _sizeText = "";
        [ObservableProperty] private string _responseBody = "";
        [ObservableProperty] private string _resolvedUrl = "";
        public ObservableCollection<KeyValuePair<string, string>> ResponseHeaders { get; } = new();

        public ApiTestingViewModel(IApiTestingService api)
        {
            _api = api;
            foreach (var c in api.Workspace.Collections) Collections.Add(new ApiCollectionItem(c));
            foreach (var h in api.Workspace.History) History.Add(new ApiHistoryItem(h));
            foreach (var e in api.Workspace.Environments) Environments.Add(e);
            SelectedCollection = Collections.FirstOrDefault();
            SelectedEnvironment = Environments.FirstOrDefault(e => e.Name == api.Workspace.ActiveEnvironment) ?? Environments.FirstOrDefault();
            var first = SelectedCollection?.Requests.FirstOrDefault();
            if (first != null) Load(first); else NewRequest();
        }

        partial void OnSelectedEnvironmentChanged(ApiEnvironment? value)
        {
            EnvVariables.Clear();
            if (value == null) return;
            foreach (var v in value.Variables) EnvVariables.Add(new KvRow(v));
            _api.Workspace.ActiveEnvironment = value.Name;
            _api.Save();
        }

        private void Load(ApiRequest r)
        {
            _editingId = r.Id;
            RequestName = r.Name; Method = r.Method; Url = r.Url;
            BodyKind = r.BodyKind; Body = r.Body;
            Auth = r.Auth; Token = r.Token; ApiKeyName = r.ApiKeyName; ApiKeyValue = r.ApiKeyValue; ApiKeyInQuery = r.ApiKeyInQuery;
            BasicUser = r.BasicUser; BasicPassword = r.BasicPassword;
            Fill(QueryRows, r.Query); Fill(HeaderRows, r.Headers); Fill(FormRows, r.Form);
        }

        private static void Fill(ObservableCollection<KvRow> rows, IEnumerable<KeyValue> items)
        {
            rows.Clear();
            foreach (var i in items) rows.Add(new KvRow(i));
        }

        private static List<KeyValue> Rows(IEnumerable<KvRow> rows) =>
            rows.Where(r => !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)).Select(r => r.ToModel()).ToList();

        private ApiRequest Build() => new()
        {
            Id = _editingId ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(RequestName) ? "Untitled" : RequestName.Trim(),
            Method = Method, Url = Url ?? "",
            Query = Rows(QueryRows), Headers = Rows(HeaderRows), Form = Rows(FormRows),
            BodyKind = BodyKind, Body = Body ?? "",
            Auth = Auth, Token = Token ?? "", ApiKeyName = ApiKeyName ?? "", ApiKeyValue = ApiKeyValue ?? "", ApiKeyInQuery = ApiKeyInQuery,
            BasicUser = BasicUser ?? "", BasicPassword = BasicPassword ?? "",
        };

        [RelayCommand]
        private async Task Send()
        {
            if (IsSending) { _cts?.Cancel(); return; }
            var req = Build();
            IsSending = true;
            _cts = new CancellationTokenSource();
            ResolvedUrl = _api.Resolve(req.Url, SelectedEnvironment);
            try
            {
                var res = await _api.SendAsync(req, SelectedEnvironment, _cts.Token);
                HasResponse = true;
                IsSuccess = res.Error == null && res.Status is >= 200 and < 400;
                StatusText = res.Error != null ? "Error" : $"{res.Status} {res.Reason}";
                TimeText = $"{res.Milliseconds} ms";
                SizeText = res.SizeBytes < 1024 ? $"{res.SizeBytes} B" : $"{res.SizeBytes / 1024.0:0.0} KB";
                ResponseBody = res.Error ?? res.Body;
                ResponseHeaders.Clear();
                foreach (var h in res.Headers) ResponseHeaders.Add(h);

                var entry = new ApiHistoryEntry { Request = req.Clone(), Status = res.Status, Milliseconds = res.Milliseconds };
                _api.Workspace.History.Insert(0, entry);
                History.Insert(0, new ApiHistoryItem(entry));
                while (History.Count > 100) History.RemoveAt(History.Count - 1);
                _api.Save();
            }
            catch (OperationCanceledException) { StatusText = "Cancelled"; IsSuccess = false; HasResponse = true; ResponseBody = ""; }
            finally { IsSending = false; }
        }

        [RelayCommand]
        private void NewRequest()
        {
            _editingId = null;
            Load(new ApiRequest { Url = "{{baseUrl}}/" });
            _editingId = null;
        }

        [RelayCommand]
        private void OpenRequest(ApiRequest? r)
        {
            if (r == null) return;
            SelectedCollection = Collections.FirstOrDefault(c => c.Requests.Contains(r)) ?? SelectedCollection;
            Load(r);
        }

        [RelayCommand]
        private void OpenHistory(ApiHistoryItem? h)
        {
            if (h == null) return;
            Load(h.Model.Request.Clone());
            _editingId = null; // re-running from history never overwrites a saved request
        }

        [RelayCommand]
        private void SaveRequest()
        {
            var col = SelectedCollection ?? AddCollection("My API");
            var req = Build();
            var existing = Collections.SelectMany(c => c.Requests.Select(r => (c, r))).FirstOrDefault(x => x.r.Id == req.Id);
            if (_editingId != null && existing.r != null)
            {
                var i = existing.c.Requests.IndexOf(existing.r);
                existing.c.Requests[i] = req;
                existing.c.Model.Requests[existing.c.Model.Requests.FindIndex(x => x.Id == req.Id)] = req;
            }
            else
            {
                col.Requests.Add(req);
                col.Model.Requests.Add(req);
            }
            _editingId = req.Id;
            _api.Save();
        }

        [RelayCommand]
        private void DeleteRequest(ApiRequest? r)
        {
            if (r == null) return;
            foreach (var c in Collections)
                if (c.Requests.Remove(r)) c.Model.Requests.RemoveAll(x => x.Id == r.Id);
            if (_editingId == r.Id) _editingId = null;
            _api.Save();
        }

        [RelayCommand]
        private void NewCollection() => SelectedCollection = AddCollection($"Collection {Collections.Count + 1}");

        private ApiCollectionItem AddCollection(string name)
        {
            var model = new ApiCollection { Name = name };
            _api.Workspace.Collections.Add(model);
            var item = new ApiCollectionItem(model);
            Collections.Add(item);
            _api.Save();
            return item;
        }

        [RelayCommand]
        private void DeleteCollection(ApiCollectionItem? c)
        {
            if (c == null) return;
            Collections.Remove(c);
            _api.Workspace.Collections.Remove(c.Model);
            if (SelectedCollection == c) SelectedCollection = Collections.FirstOrDefault();
            _api.Save();
        }

        [RelayCommand]
        private void ClearHistory()
        {
            History.Clear();
            _api.Workspace.History.Clear();
            _api.Save();
        }

        [RelayCommand] private void ToggleEnvironmentEditor() => IsEnvironmentEditorOpen = !IsEnvironmentEditorOpen;

        [RelayCommand]
        private void SaveEnvironment()
        {
            if (SelectedEnvironment == null) return;
            SelectedEnvironment.Variables = Rows(EnvVariables);
            _api.Save();
            IsEnvironmentEditorOpen = false;
        }

        [RelayCommand]
        private void AddEnvironment()
        {
            var env = new ApiEnvironment
            {
                Name = $"Environment {Environments.Count + 1}",
                Variables = new() { new() { Key = "baseUrl", Value = "http://localhost" } },
            };
            _api.Workspace.Environments.Add(env);
            Environments.Add(env);
            SelectedEnvironment = env;
            IsEnvironmentEditorOpen = true;
        }

        [RelayCommand] private void SaveCollections() => _api.Save();
    }
}
