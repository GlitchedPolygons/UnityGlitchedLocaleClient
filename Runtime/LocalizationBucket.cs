using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace GlitchedPolygons.Localization
{
    /// <summary>
    /// A localization bucket is a component that contains a dictionary of localized key-value string pairs for a list of user-defined locales.<para> </para>
    /// It connects to an instance of the Glitched Locale Server to allow users to change translations at runtime without requiring to submit a new package update/release everytime a typo is corrected...<para> </para>
    /// <seealso cref="LocalizedText"/>
    /// More information available on:
    /// https://glitchedpolygons.com/store/software/glitched-locale-server
    /// </summary>
    public class LocalizationBucket : MonoBehaviour
    {
        /// <summary>
        /// DTO for requesting translations.
        /// </summary>
        protected sealed class TranslationRequestDto
        {
            /// <summary>
            /// Glitched Locale Server User ID.
            /// </summary>
            public string UserId;

            /// <summary>
            /// [OPTIONAL] Read-access password (if the user account matching above <see cref="UserId"/> has one set up).
            /// </summary>
            public string ReadAccessPassword;

            /// <summary>
            /// [OPTIONAL] Unix-timestamp of when we requested translations for this <see cref="LocalizationBucket"/> for the last time. <para> </para>
            /// This is to avoid over-fetching data and reduce bandwidth if possible (ideally, the backend only ever returns stuff that's changed since the last fetch).
            /// </summary>
            public long? LastFetchUTC;

            /// <summary>
            /// The keys of all the translations you want to download from the localization server.
            /// </summary>
            public List<string> Keys;

            /// <summary>
            /// The list of locales for which to download the translations.
            /// </summary>
            public List<string> Locales;
        }

        /// <summary>
        /// Response DTO for the translation endpoint's response body item type.
        /// </summary>
        protected sealed class TranslationEndpointResponseDto
        {
            /// <summary>
            /// Translation key.
            /// </summary>
            public string Key;

            /// <summary>
            /// Dictionary of translations containing the locale as dictionary key, and the corresponding localized string as value.
            /// </summary>
            public Dictionary<string, string> Translations;
        }

        /// <summary>
        /// Error structure to return in response bodies.
        /// </summary>
        protected sealed class Error
        {
            /// <summary>
            /// The error code (clients can switch on this number to for example pick a corresponding translated error message).
            /// </summary>
            public int Code { get; set; }

            /// <summary>
            /// Raw server-side error message to the client (in English).
            /// </summary>
            public string Message { get; set; }

            /// <summary>
            /// Parameterless ctor.
            /// </summary>
            public Error()
            {
                //nop
            }

            /// <summary>
            /// Creates an <see cref="Error"/> using a specific error code and message.
            /// </summary>
            /// <param name="code">Internal API error code.</param>
            /// <param name="message">Server-side error message (in English).</param>
            public Error(int code, string message)
            {
                Code = code;
                Message = message;
            }
        }

        /// <summary>
        /// If <see cref="Items"/> is not <c>null</c> or empty, it means that the request was successful. <para> </para>
        /// Failed requests should return the correct HTTP status code, but (if applicable) still return one or more errors inside <see cref="Errors"/>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        protected sealed class ResponseBodyDto<T>
        {
            /// <summary>
            /// The type of the items returned. Can be <c>null</c> if the request was successful but nothing's returned (e.g. a status <c>201</c>).
            /// </summary>
            public string Type { get; set; } = null;

            /// <summary>
            /// The total amount of items potentially available to fetch.
            /// </summary>
            public long Count { get; set; } = 0;

            /// <summary>
            /// This and <see cref="Errors"/> are mutually exclusive: if this is set, <see cref="Errors"/> should be <c>null</c> and vice-versa!<para> </para>
            /// This can be <c>null</c> if the request succeeded and there are no items to return.
            /// </summary>
            public T[] Items { get; set; } = null;

            /// <summary>
            /// If the request failed, one or potentially more errors CAN be written into this array for the client to handle.
            /// </summary>
            public Error[] Errors { get; set; } = null;

            /// <summary>
            /// Parameterless ctor.
            /// </summary>
            public ResponseBodyDto()
            {
                //nop
            }

            /// <summary>
            /// Spawn an error response.
            /// </summary>
            /// <param name="errors">One or more errors.</param>
            /// <seealso cref="Error"/>
            public ResponseBodyDto(params Error[] errors)
            {
                Errors = errors;
            }
        }

        /// <summary>
        /// Default base URL that points to the official Glitched Polygons Locale Server.
        /// Its frontend is reachable under: https://locales.glitchedpolygons.com
        /// </summary>
        public const string DEFAULT_LOCALE_SERVER_BASE_URL = "https://api.locales.glitchedpolygons.com";

        /// <summary>
        /// Default endpoint path to use for fetching translations from the server.
        /// </summary>
        public const string DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT = "/api/v1/translations/translate";

        /// <summary>
        /// This event is raised when the <see cref="LocalizationBucket"/> refreshed its dictionary of translations.<para> </para>
        /// Interested scripts should subscribe to this event in order to know that they should refresh their labels/UI/usages of the translations.
        /// </summary>
        public event Action Refreshed;

        /// <summary>
        /// This event is raised whenever the <see cref="SetLocale"/> method is called, such that interested subscribers know when to refresh their labels in the UI to reflect the new language setting.
        /// </summary>
        public event Action<string> ChangedLocale;

        /// <summary>
        /// This event is raised when a connection attempt to the locale server resulted in a failure.
        /// </summary>
        public event Action FailedConnectionToLocaleServer;

        [SerializeField]
        private string bucketId = Guid.NewGuid().ToString("N");

        [SerializeField]
        private string userId = string.Empty;

        [SerializeField]
        private string apiKey = string.Empty;

        [SerializeField]
        private string readAccessPassword = string.Empty;

        [SerializeField]
        private string localeServerBaseUrl = DEFAULT_LOCALE_SERVER_BASE_URL;

        [SerializeField]
        private string localeServerTranslationEndpoint = DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT;

        [SerializeField]
        [Range(1, 1000)]
        private int minSecondsBetweenRequests = 120;

        [SerializeField]
        [Range(64, 8192)]
        private int maxRefreshResponseTimeMilliseconds = 4096;

        [SerializeField]
        private bool usePlayerPrefs = true;

        [SerializeField]
        private string playerPrefsIdLocaleIndex = "LocaleIndex";

        [SerializeField]
        private string playerPrefsIdLastFetchUTC = "LastFetchUTC";

        [SerializeField]
        private string localizationCacheDirectoryName = "LocalizationCache";

        [SerializeField]
        private List<string> locales = new()
        {
            "en_US.UTF-8",
            "de_DE.UTF-8",
            "it_IT.UTF-8",
        };

        [SerializeField]
        private List<string> keys = new();

        private int localeIndex = 0;

        private string cacheDirectory = null;

        private ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache = new();

        private long? lastFetchUTC = null;

        private bool refreshing = false;

        private readonly HttpClient httpClient = new();

        private void Awake()
        {
            if (usePlayerPrefs)
            {
                localeIndex = PlayerPrefs.GetInt(playerPrefsIdLocaleIndex, 0);

                lastFetchUTC = long.TryParse(PlayerPrefs.GetString($"{playerPrefsIdLastFetchUTC}_{bucketId}", null), out long storedLastFetchUTC) ? storedLastFetchUTC : null;
            }

            cacheDirectory = Path.Combine(Application.persistentDataPath, localizationCacheDirectoryName);

            if (!Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            httpClient.BaseAddress = new Uri(localeServerBaseUrl);

            LoadCacheFromDisk();
        }

        /// <summary>
        /// Checks whether or not the locale server defined in the <see cref="LocalizationBucket"/>'s base URL field is online and reachable.
        /// </summary>
        /// <returns><see cref="Task{HttpResponseMessage}"/> - Use <see cref="HttpResponseMessage"/>.<see cref="HttpResponseMessage.IsSuccessStatusCode"/> to find out whether or not the server is reachable. The returned response body furthermore contains the server's public RSA key.</returns>
        public Task<HttpResponseMessage> IsLocaleServerReachable()
        {
            return Task.Run(() => httpClient.GetAsync("/api/v1/keys/rsa/public"));
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnDisable()
        {
            WriteCacheToDisk();
        }

        [ContextMenu("Gather all translation keys from scene")]
        private void GatherAllTranslationKeysFromScene()
        {
            foreach (LocalizedText localizedText in Resources.FindObjectsOfTypeAll<LocalizedText>())
            {
                string localizationKey = localizedText.GetLocalizationKey();

                if (localizedText.IsInBucket(this) && !keys.Contains(localizationKey))
                {
                    keys.Add(localizedText.GetLocalizationKey());
                }
            }

#if UNITY_EDITOR
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
#endif
        }

        private void OnApplicationQuit()
        {
            WriteCacheToDisk();

            if (usePlayerPrefs)
            {
                PlayerPrefs.SetInt(playerPrefsIdLocaleIndex, localeIndex);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// Change the locale server to fetch translations from. This will trigger a <see cref="Refresh"/>!
        /// Make sure that the new server you want to point the <see cref="LocalizationBucket"/> to is online and reachable!
        /// </summary>
        /// <param name="newLocaleServerBaseUrl">The new base URL </param>
        /// <param name="translationEndpoint"></param>
        /// <returns><c>true</c> if the server was changed, <c>false</c> if no changes were made.</returns>
        public bool ChangeServer(string newLocaleServerBaseUrl = DEFAULT_LOCALE_SERVER_BASE_URL, string translationEndpoint = DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT)
        {
            if (newLocaleServerBaseUrl == localeServerBaseUrl && translationEndpoint == localeServerTranslationEndpoint)
            {
                return false;
            }

            localeServerBaseUrl = newLocaleServerBaseUrl;
            localeServerTranslationEndpoint = translationEndpoint;
            httpClient.BaseAddress = new Uri(localeServerBaseUrl);
            Refresh();

            return true;
        }

        /// <summary>
        /// Gets the list of currently enabled locales in the <see cref="LocalizationBucket"/>
        /// </summary>
        /// <returns><see cref="IReadOnlyCollection{String}"/></returns>
        public IReadOnlyCollection<string> GetLocales()
        {
            return locales;
        }

        /// <summary>
        /// Changes the <see cref="LocalizationBucket"/>'s active locale setting.<para> </para>
        /// The <see cref="ChangedLocale"/> event will be raised and all the <see cref="LocalizedText"/>s in the scene
        /// will need to refresh their UI labels as well as all the other scripts that make use of translations from this bucket. 
        /// </summary>
        /// <param name="locale">The new locale to use (e.g. <c>en_US.UTF-8</c>). This value must be in the list of enabled locales (use <see cref="GetLocales"/> to find out which locales are currently enabled in the <see cref="LocalizationBucket"/>).</param>
        /// <returns>Whether or not the locale change was successfully applied.</returns>
        public bool SetLocale(string locale)
        {
            if (locales.All(l => l != locale))
            {
                return false;
            }

            localeIndex = locales.IndexOf(locale);

            ChangedLocale?.Invoke(locale);

            if (usePlayerPrefs)
            {
                PlayerPrefs.SetInt(playerPrefsIdLocaleIndex, localeIndex);
                PlayerPrefs.Save();
            }

            return true;
        }

        /// <summary>
        /// Adds a new locale to the <see cref="LocalizationBucket"/>.
        /// </summary>
        /// <remarks>
        /// Note: this will NOT trigger a <see cref="Refresh"/>! You need to call that method manually once the call to this one here succeeds.
        /// </remarks>
        /// <param name="locale">Locale string. For example: <c>en_US.UTF-8</c> for American English, <c>en_GB.UTF-8</c> for British English, etc...</param>
        /// <returns>Whether or not the locale insertion was successful.</returns>
        public bool AddLocale(string locale)
        {
            if (locales.Contains(locale))
            {
                return false;
            }

            locales.Add(locale);
            return true;
        }

        /// <summary>
        /// Removes a locale from the list of enabled locales in the <see cref="LocalizationBucket"/>.
        /// </summary>
        /// <remarks>
        /// Note: this will NOT trigger a <see cref="Refresh"/>! You need to call that method manually once the call to this one here succeeds.
        /// </remarks>
        /// <param name="locale">Locale string. For example: <c>en_US.UTF-8</c> for American English, <c>en_GB.UTF-8</c> for British English, etc...</param>
        /// <returns>Whether or not the locale removal was successful.</returns>
        public bool RemoveLocale(string locale)
        {
            if (locales.All(l => l != locale))
            {
                return false;
            }

            locales.RemoveAt(locales.IndexOf(locale));
            return true;
        }

        /// <summary>
        /// Refreshes the translations, contacting the locale server if required.
        /// </summary>
        public void Refresh()
        {
            if (keys.Count == 0)
            {
                return;
            }

            long utcNow = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

            if (!cache.IsEmpty && lastFetchUTC.HasValue && utcNow - lastFetchUTC < minSecondsBetweenRequests)
            {
                return;
            }

            StartCoroutine(RefreshCoroutine());
        }

        private IEnumerator RefreshCoroutine()
        {
            if (refreshing)
            {
                yield break;
            }

            refreshing = true;

            long? storedLastFetchUTC = lastFetchUTC;

            lastFetchUTC = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

            if (usePlayerPrefs)
            {
                PlayerPrefs.SetString($"{playerPrefsIdLastFetchUTC}_{bucketId}", lastFetchUTC.Value.ToString());
                PlayerPrefs.Save();
            }

            var task = Task.Run(async () =>
            {
                TranslationRequestDto dto = new()
                {
                    Keys = keys,
                    UserId = userId,
                    Locales = locales,
                    ReadAccessPassword = readAccessPassword,
                    LastFetchUTC = storedLastFetchUTC,
                };

                using var httpContent = new StringContent(JsonUtility.ToJson(dto), Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(apiKey))
                {
                    httpContent.Headers.Add("API-Key", apiKey);
                }

                HttpResponseMessage response = await httpClient.PostAsync(localeServerTranslationEndpoint, httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                string json = await response.Content.ReadAsStringAsync();

                ResponseBodyDto<TranslationEndpointResponseDto> responseBodyDto = JsonConvert.DeserializeObject<ResponseBodyDto<TranslationEndpointResponseDto>>(json);

                if (responseBodyDto != null)
                {
                    foreach (TranslationEndpointResponseDto translation in responseBodyDto.Items)
                    {
                        if (cache.ContainsKey(translation.Key))
                        {
                            ConcurrentDictionary<string, string> cachedTranslation = cache[translation.Key];

                            foreach ((string key, string value) in translation.Translations)
                            {
                                cachedTranslation[key] = value;
                            }
                        }
                        else
                        {
                            cache[translation.Key] = new ConcurrentDictionary<string, string>(translation.Translations);
                        }
                    }
                }
            });

            WaitForSecondsRealtime millisecondsBetweenChecks = new(0.25f);

            while (!task.IsCompleted)
            {
                yield return millisecondsBetweenChecks;

                if (new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() - (lastFetchUTC * 1000) > maxRefreshResponseTimeMilliseconds)
                {
                    FailedConnectionToLocaleServer?.Invoke();
                    refreshing = false;
                    yield break;
                }
            }

            refreshing = false;
            Refreshed?.Invoke();
        }

        /// <summary>
        /// Useful indexer for getting a translation from the bucket directly with the square brackets operator.
        /// </summary>
        /// <param name="key">Translation key.</param>
        public string this[string key] => Translate(key);

        /// <summary>
        /// Gets the translated string value for a specific translation key.<para> </para>
        /// Note that translation keys must be in the <see cref="LocalizationBucket"/>'s list of translation keys in order for them to be fetched from the locale server!<para> </para>
        /// Use the <see cref="LocalizationBucket"/>'s inspector for that: there is a context menu action that scans the scene for missing keys. Use it every time you added new translation usages to your scene to prevent untranslated values in the final product!
        /// </summary>
        /// <param name="key">Translation key.</param>
        /// <returns><c>null</c> if the translation couldn't be found in the <see cref="LocalizationBucket"/>'s dictionary; the translated string value otherwise.</returns>
        public string Translate(string key)
        {
            if (!cache.TryGetValue(key, out ConcurrentDictionary<string, string> translations))
            {
                return null;
            }

            if (!translations.TryGetValue(locales[localeIndex], out string translation))
            {
                return null;
            }

            return translation;
        }

        /// <summary>
        /// Forces the localization cache to be written to disk immediately.
        /// </summary>
        public void WriteCacheToDisk()
        {
            _ = Task.Run(() =>
            {
                using var fileStream = new FileStream(Path.Combine(cacheDirectory, bucketId), FileMode.OpenOrCreate);
                using var brotli = new BrotliStream(fileStream, CompressionLevel.Optimal, false);
                
                string json = JsonConvert.SerializeObject(cache, Formatting.None);
                
                brotli.Write(Encoding.UTF8.GetBytes(json));
            });
        }

        /// <summary>
        /// Loads the localization dictionary cache from disk into the <see cref="LocalizationBucket"/> instance. 
        /// </summary>
        public void LoadCacheFromDisk()
        {
            StartCoroutine(LoadCacheFromDiskCoroutine());
        }

        private IEnumerator LoadCacheFromDiskCoroutine()
        {
            string cacheFilePath = Path.Combine(cacheDirectory, bucketId);

            if (File.Exists(cacheFilePath))
            {
                Task loadTask = Task.Run(() =>
                {
                    using FileStream fileStream = new(cacheFilePath, FileMode.OpenOrCreate);
                    using BrotliStream brotli = new(fileStream, CompressionMode.Decompress);
                    using MemoryStream memoryStream = new();

                    brotli.CopyTo(memoryStream);

                    string json = Encoding.UTF8.GetString(memoryStream.ToArray());

                    cache = JsonConvert.DeserializeObject<ConcurrentDictionary<string, ConcurrentDictionary<string, string>>>(json);
                });

                DateTime startedLoadingUTC = DateTime.UtcNow;
                WaitForSecondsRealtime wait = new(0.1f);

                while (!loadTask.IsCompleted && DateTime.UtcNow - startedLoadingUTC < TimeSpan.FromSeconds(2.0d))
                {
                    yield return wait;
                }
            }
            else
            {
                Refresh();
            }

            Refreshed?.Invoke();
        }
    }
}

// Copyright (C) Raphael Beck, 2022 | https://glitchedpolygons.com