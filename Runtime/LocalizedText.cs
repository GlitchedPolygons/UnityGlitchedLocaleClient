using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GlitchedPolygons.Localization
{
    /// <summary>
    /// This is a component that references a <see cref="LocalizationBucket"/>
    /// and hooks into its dictionary to translate either a UI <c>Text</c> or a <c>TMP_Text</c> component's string value.
    /// </summary>
    public class LocalizedText : MonoBehaviour
    {
        [SerializeField]
        private LocalizationBucket localizationBucket;

        [SerializeField]
        private string localizationKey = string.Empty;

        [SerializeField]
        [TextArea(4, 8)]
        private string fallbackValue = string.Empty;

        private Text text;
        private TMP_Text tmpText;

        private void Awake()
        {
            text = GetComponent<Text>();
            tmpText = GetComponent<TMP_Text>();

            if (!string.IsNullOrEmpty(fallbackValue))
            {
                if (text != null)
                {
                    text.text = fallbackValue;
                }

                if (tmpText != null)
                {
                    tmpText.text = fallbackValue;
                }
            }
        }

        private void OnEnable()
        {
            localizationBucket.Refreshed += LocalizationBucketOnRefreshed;
            localizationBucket.ChangedLocale += LocalizationBucketOnChangedLocale;

            LocalizationBucketOnRefreshed();
        }

        private void OnDisable()
        {
            localizationBucket.Refreshed -= LocalizationBucketOnRefreshed;
            localizationBucket.ChangedLocale -= LocalizationBucketOnChangedLocale;
        }

        private void LocalizationBucketOnRefreshed()
        {
            string newTranslation = localizationBucket[localizationKey];

            if (string.IsNullOrEmpty(newTranslation))
            {
                return;
            }

            if (text != null)
            {
                text.text = newTranslation;
            }

            if (tmpText != null)
            {
                tmpText.text = newTranslation;
            }
        }

        private void LocalizationBucketOnChangedLocale(string newLocale)
        {
            LocalizationBucketOnRefreshed();
        }

        /// <summary>
        /// Checks whether or not this <see cref="LocalizedText"/> is hooked into a specific instance of a <see cref="LocalizationBucket"/>.
        /// </summary>
        /// <param name="bucket">The bucket to check against.</param>
        /// <returns><paramref name="bucket"/> == <see cref="localizationBucket"/></returns>
        public bool IsInBucket(LocalizationBucket bucket)
        {
            return bucket == localizationBucket;
        }

        /// <summary>
        /// Gets the localization key configured in this <see cref="LocalizedText"/> instance.
        /// </summary>
        /// <returns><see cref="localizationKey"/></returns>
        public string GetLocalizationKey()
        {
            return localizationKey;
        }
    }
}

// Copyright (C) Raphael Beck, 2022 | https://glitchedpolygons.com