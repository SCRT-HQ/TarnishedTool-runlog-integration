// 

using TarnishedTool.ViewModels;

namespace TarnishedTool.Models;

public class StringIndexedNumberPair(string stringIndex, float value) : BaseViewModel
{
    private string _stringIndex = stringIndex;
    public string StringIndex
    {
        get => _stringIndex;
        set => SetProperty(ref _stringIndex, value);
    }

    private float _value = value;
    public float Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}