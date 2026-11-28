using UnityEngine;


namespace Scripts.Utils
{
    public class ComponentUtil
    {
        //컴포넌트 찾고 없으면 추가(컴포넌트여야하기에 where T : Unity의 컴포넌트로 정해두기)
        public static T GetOrAddComponent<T>(GameObject go) where T : UnityEngine.Component
        {
            T component = go.GetComponent<T>();

            if (component.IsNull() == true)
                component = go.AddComponent<T>();

            return component;
        }

        //GameObject는 컴포넌트가 아니므로 Transform을 통해 접근.
        public static GameObject FindChild(GameObject go, string name = null, bool recursive = false)
        {
            Transform transform = FindChild<Transform>(go, name, recursive);
            if (transform.IsNull() == true)
                return null;

            return transform.gameObject;
        }

        //자식 객체 찾기(유니티가 관리하는 객체(Object) 타입만 오게하기)
        public static T FindChild<T>(GameObject go, string name = null, bool recursive = false) where T : UnityEngine.Object
        {
            if (go.IsNull() == true) return null;


            if (recursive == false)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    Transform transform = go.transform.GetChild(i);

                    if (string.IsNullOrEmpty(name) || transform.name == name)
                    {
                        T component = transform.GetComponent<T>();
                        if (component.IsNull() == false)
                            return component;
                    }
                }
            }
            else
            {
                foreach (T component in go.GetComponentsInChildren<T>())
                {
                    if (string.IsNullOrEmpty(name) || component.name == name)
                        return component;
                }
            }

            return null;
        }
    }
}