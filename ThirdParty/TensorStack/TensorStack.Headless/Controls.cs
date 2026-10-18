using System;
namespace TensorStack.WPF.Controls {
 public record OpenViewArgs;
    public class ProgressInfo : BaseModel
    {
        private int _value;
        private int _maximum = 1;
        private string _message;

        public int Value
        {
            get { return _value; }
            set { SetProperty(ref _value, value); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { SetProperty(ref _maximum, value); }
        }

        public string Message
        {
            get { return _message; }
            set { SetProperty(ref _message, value); }
        }


        public void Update(string message = default)
        {
            Message = message;
        }


        public void Update(int value, int maximum, string message = default)
        {
            Value = value;
            Message = message;
            Maximum = Math.Max(1, maximum);
        }


        public void Indeterminate(string message = default)
        {
            Value = 0;
            Maximum = -1;
            Message = message;
        }


        public void Clear()
        {
            Value = 0;
            Maximum = 1;
            Message = null;
        }
    }
}
