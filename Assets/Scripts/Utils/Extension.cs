using System;
using UnityEngine;

namespace Scripts.Utils
{
    public static class Extension
    {
        public static T GetOrAddComponent<T>(this GameObject go) where T : UnityEngine.Component
            => ComponentUtil.GetOrAddComponent<T>(go);

        #region  null 체크
        // 유니티 오브젝트
        public static bool IsNull(this UnityEngine.Object go) { return ReferenceEquals(go, null); }
        //C# 오브젝트
        public static bool IsNull(this System.Object go) { return ReferenceEquals(go, null); }
        #endregion

        //객체 유효성 체크
        public static bool IsValid(this GameObject go) { return go.IsNull() == false && go.activeSelf == true; }

    }
}