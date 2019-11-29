using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace LegendasTV
{
    class LegendasTVProvider : ISubtitleProvider, IDisposable
    {
        public static readonly string URL_BASE = "http://legendas.tv/";
        public static readonly string URL_DOWNLOAD = URL_BASE + "info.php?d=%s&c=1";
        public static readonly string URL_LOGIN = URL_BASE + "login";

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IServerConfigurationManager _config;
        private readonly IEncryptionManager _encryption;
        private readonly IJsonSerializer _json;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IJsonSerializer _jsonSerializer;
        private const string PasswordHashPrefix = "h:";

        public string Name => "Legendas TV";


        public LegendasTVProvider(ILogger logger, IHttpClient httpClient, IServerConfigurationManager config, IEncryptionManager encryption, IJsonSerializer json, IFileSystem fileSystem, ILocalizationManager localizationManager, ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _httpClient = httpClient;
            _config = config;
            _encryption = encryption;
            _json = json;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _localizationManager = localizationManager;
            _jsonSerializer = jsonSerializer;

            _config.NamedConfigurationUpdating += _config_NamedConfigurationUpdating;

            // Load HtmlAgilityPack from embedded resource
            EmbeddedAssembly.Load(GetType().Namespace + ".HtmlAgilityPack.dll", "HtmlAgilityPack.dll");
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler((object sender, ResolveEventArgs args) => EmbeddedAssembly.Get(args.Name));
        }

        private LegendasTVOptions GetOptions() => _config.GetLegendasTVConfiguration();

        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            if (request.IsForced.HasValue || request.IsPerfectMatch)
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            if (await Login())
            {
                BaseItem item = _libraryManager.FindByPath(request.MediaPath, false);

                string imdbId = item.ProviderIds["Imdb"].Substring(2);

                var legendatvId = await FindId(request, imdbId, cancellationToken);

                return Array.Empty<RemoteSubtitleInfo>();
            }
            else
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }
        }

        private async Task<int> FindId(SubtitleSearchRequest request, string imdbId, CancellationToken cancellationToken, int depth = 0)
        {
            BaseItem item = _libraryManager.FindByPath(request.MediaPath, false);
            var query = "";
            switch (request.ContentType)
            {
                case VideoContentType.Episode:
                    query = String.Format("{0}", request.SeriesName, request.ParentIndexNumber, request.IndexNumber);
                    break;
                case VideoContentType.Movie:
                    query = item.OriginalTitle;
                    break;
            }

            var requestOptions = new HttpRequestOptions()
            {
                Url = string.Format(URL_BASE + "legenda/sugestao/{0}", HttpUtility.HtmlEncode(query)),
                CancellationToken = cancellationToken,
            };

            using (var stream = await _httpClient.Get(requestOptions))
            {
                using (var reader = new StreamReader(stream))
                {
                    var response = reader.ReadToEnd();
                    _logger.Info(response);
                    var suggestions = _jsonSerializer.DeserializeFromString<List<LegendasTVSuggestion>>(response);
                    _logger.Info(response.ToString());
                    foreach (var suggestion in suggestions)
                    {
                        var source = suggestion.source;
                        _logger.Info(source.ToString());
                        if (source.id_imdb == imdbId)
                        {

                        }
                    }
                    return -1;
                }

            }
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
            CancellationToken cancellationToken,
            string query = "-",
            string lang = "-",
            string page = "-",
            string itemId = "-"
            )
        {
            var requestOptions = new HttpRequestOptions()
            {
                Url = string.Format(URL_BASE + "legenda/busca/{0}/{1}/-/{2}/{3}", HttpUtility.HtmlEncode(query), lang, page, itemId),
                CancellationToken = cancellationToken,
                Referer = URL_BASE + "busca/" + query
            };
            requestOptions.RequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            using (var stream = await _httpClient.Get(requestOptions))
            {
                using (var reader = new StreamReader(stream))
                {
                    var response = reader.ReadToEnd();
                    // TODO: parse resulting HTML
                    return Array.Empty<RemoteSubtitleInfo>();
                }
            }
        }

        public async Task<bool> Login()
        {
            var options = GetOptions();
            var username = options.LegendasTVUsername;
            var password = DecryptPassword(options.LegendasTVPasswordHash);
            string result = await SendPost(URL_LOGIN, string.Format("data[User][username]={0}&data[User][password]={1}", username, password));

            if (result.Contains("Usuário ou senha inválidos"))
            {
                _logger.Error("Invalid username or password");
                return false;
            }

            return true;
        }

        private async Task<string> SendPost(string url, string postData)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(postData);

            var requestOptions = new HttpRequestOptions()
            {
                Url = url,
                RequestContentType = "application/x-www-form-urlencoded",
                RequestContentBytes = bytes
            };

            var response = await _httpClient.Post(requestOptions);

            using (var stream = response.Content)
            {
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        void _config_NamedConfigurationUpdating(object sender, ConfigurationUpdateEventArgs e)
        {
            if (!string.Equals(e.Key, "legendastv", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = (LegendasTVOptions)e.NewConfiguration;

            if (options != null &&
                !string.IsNullOrWhiteSpace(options.LegendasTVPasswordHash) &&
                !options.LegendasTVPasswordHash.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                options.LegendasTVPasswordHash = EncryptPassword(options.LegendasTVPasswordHash);
            }
        }

        private string EncryptPassword(string password)
        {
            return PasswordHashPrefix + _encryption.EncryptString(password);
        }

        private string DecryptPassword(string password)
        {
            if (password == null ||
                !password.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _encryption.DecryptString(password.Substring(2));
        }

        public void Dispose() => GC.SuppressFinalize(this);

        ~LegendasTVProvider() => Dispose();
    }

    class LegendasTVSuggestion
    {
        public class LegendasTVSource
        {
            public string id_filme = null;
            public string id_imdb = null;
            public string tipo = null;
            public string int_genero = null;
            public string dsc_imagen = null;
            public string dsc_nome = null;
            public string dsc_sinopse = null;
            public string dsc_data_lancamento = null;
            public string dsc_url_imdb = null;
            public string dsc_nome_br = null;
            public string soundex = null;
            public string temporada = null;
            public string id_usuario = null;
            public string flg_liberado = null;
            public string dsc_data_liberacao = null;
            public string dsc_data = null;
            public string dsc_metaphone_us = null;
            public string dsc_metaphone_br = null;
            public string episodios = null;
            public string flg_seriado = null;
            public string last_used = null;
            public string deleted = null;
        }

        public string index = null;
        public string type = null;
        public string id = null;
        public string score = null;
        public LegendasTVSource source = null;
    }
}