using UnityEngine;
using System.Collections;


public enum NOOVis
{
    IGNORE,
    PROCESS
}

public class NOOVisibility : MonoBehaviour
{
    public NOOVis visibility = NOOVis.IGNORE;
}
