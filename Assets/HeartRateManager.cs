using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HeartRateManager : MonoBehaviour
{
    private static IHeartRateManager instance = null;
    private static readonly object padlock = new object();
    HeartRateManager() { }
    public static IHeartRateManager Instance
    {
        get
        {
            lock (padlock)
            {
                if (instance == null)
                {
#if UNITY_STANDALONE_WIN
                    instance = new HeartRateManagerWindows();
#else
                    instance = new HeartRateManagerMobile();
#endif
                }
                return instance;
            }
        }
    }
}
