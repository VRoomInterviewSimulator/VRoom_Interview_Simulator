using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace VRoom.Backend
{
    public class ScoreCellView : MonoBehaviour
    {
        public TMP_Text labelText;
        public TMP_Text valueText;

        public void Set(int index, string label, int value, int max = 10)
        {
            value = Mathf.Clamp(value, 0, max);
            if (labelText) 
                labelText.text = $"{index}. {label}";

            if (valueText)
            {
                valueText.text = $"{value}/{max}";
                valueText.color = ColorFor(value, max);
            }
        }

        private static Color ColorFor(int v, int max)
        {
            float r = max <= 0 ? 0f : (float)v / max;
            if (r >= 0.8f) 
                return new Color(0.20f, 0.60f, 0.25f); // 초록
            if (r >= 0.5f) 
                return new Color(0.85f, 0.55f, 0.00f); // 주황

            return new Color(0.80f, 0.20f, 0.18f); // 빨강
        }
    }
}