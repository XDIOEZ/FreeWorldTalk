using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldTalk
{
    /// <summary>Runtime-only entry in the existing hero information tab.</summary>
    public sealed class RoleInfoPromptButton : MonoBehaviour
    {
        private const string NodeName = "FreeWorldTalk.RolePromptButton";
        private static readonly HashSet<RoleInfoPromptButton> instances = new HashSet<RoleInfoPromptButton>();
        private FWHeroBean hero;
        private Button button;

        public static void Attach(Dialog_MainHeroInfo dialog, FWHeroBean currentHero)
        {
            if (dialog == null) throw new ArgumentNullException("dialog");
            if (currentHero == null) throw new ArgumentNullException("currentHero");
            Transform info = dialog.transform.Find("ViewInfo");
            if (info == null)
                throw new InvalidOperationException("FreeWorldTalk 角色信息页面缺少 ViewInfo。");

            Transform previous = info.Find(NodeName);
            if (previous != null)
            {
                var existing = previous.GetComponent<RoleInfoPromptButton>();
                if (existing == null)
                    throw new InvalidOperationException("FreeWorldTalk 角色提示词按钮节点被其他组件占用。");
                existing.hero = currentHero;
                return;
            }

            Transform template = dialog.transform.Find("TabPanel/Tab0");
            if (template == null || template.GetComponent<Button>() == null)
                throw new InvalidOperationException("FreeWorldTalk 角色信息页面缺少原有信息页按钮 TabPanel/Tab0。");

            GameObject created = null;
            try
            {
                created = Instantiate(template.gameObject, info, false);
                created.SetActive(false);
                created.name = NodeName;

                // The visual tree comes from the game's own tab button. Only its UI components survive;
                // copied tab/localization/game callbacks must never act on the original information page.
                foreach (MonoBehaviour behaviour in created.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour is Graphic || behaviour is Button) continue;
                    behaviour.enabled = false;
                    Destroy(behaviour);
                }
                foreach (Button nested in created.GetComponentsInChildren<Button>(true))
                {
                    nested.onClick = new Button.ButtonClickedEvent();
                    nested.navigation = new Navigation { mode = Navigation.Mode.None };
                    if (nested.gameObject != created) nested.interactable = false;
                }
                Transform off = created.transform.Find("Off");
                Transform on = created.transform.Find("On");
                if (off != null) off.gameObject.SetActive(true);
                if (on != null) on.gameObject.SetActive(false);

                var rect = (RectTransform)created.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-84f, -18f);
                rect.sizeDelta = new Vector2(220f, 36f);
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
                rect.SetAsLastSibling();

                TMP_Text[] labels = created.GetComponentsInChildren<TMP_Text>(true);
                if (labels.Length == 0)
                    throw new InvalidOperationException("FreeWorldTalk 角色信息页按钮缺少 TMP 标签。");
                foreach (TMP_Text label in labels)
                {
                    label.text = "角色扮演提示词";
                    label.richText = false;
                    label.raycastTarget = false;
                    label.enableAutoSizing = true;
                    label.fontSizeMin = 16f;
                    label.fontSizeMax = 22f;
                    label.alignment = TextAlignmentOptions.Center;
                    label.overflowMode = TextOverflowModes.Ellipsis;
                }

                var binder = created.AddComponent<RoleInfoPromptButton>();
                binder.hero = currentHero;
                binder.button = created.GetComponent<Button>();
                binder.button.interactable = true;
                binder.button.onClick.AddListener(binder.Open);
                instances.Add(binder);
                created.SetActive(true);
            }
            catch
            {
                if (created != null)
                {
                    created.SetActive(false);
                    Destroy(created);
                }
                throw;
            }
        }

        private void Open()
        {
            var runtime = TalkRuntime.Instance;
            if (runtime == null || hero == null) return;
            // showHero updates this binding every time the page's character changes.
            runtime.OpenRolePrompt(hero);
        }

        public static void Cleanup()
        {
            var snapshot = new List<RoleInfoPromptButton>(instances);
            instances.Clear();
            foreach (RoleInfoPromptButton binder in snapshot)
            {
                if (binder == null) continue;
                binder.hero = null;
                if (binder.button != null) binder.button.onClick.RemoveAllListeners();
                binder.gameObject.SetActive(false);
                Destroy(binder.gameObject);
            }
        }

        private void OnDestroy()
        {
            instances.Remove(this);
            hero = null;
            if (button != null) button.onClick.RemoveAllListeners();
        }
    }
}
