using System.Globalization;
using System.Windows.Data;

namespace CharacterConceptAI.Desktop.Components;

/// <summary>Localizes display text without changing immutable API enum values.</summary>
public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Active" => "Đang hoạt động",
        "Archived" => "Đã lưu trữ",
        "Draft" => "Bản nháp",
        "Pending" => "Đang chờ",
        "Running" => "Đang sinh ảnh",
        "Completed" => "Hoàn thành",
        "Failed" => "Thất bại",
        "Cancelled" => "Đã hủy",
        "Interrupted" => "Gián đoạn",
        "Submitted" or "UnderReview" => "Chờ duyệt",
        "Approved" => "Đã duyệt",
        "Rejected" => "Đã từ chối",
        "Owner" => "Chủ dự án",
        "Designer" => "Thiết kế",
        "Viewer" => "Chỉ xem",
        "true" or "True" => "Có",
        "false" or "False" => "Không",
        _ => value?.ToString() ?? string.Empty
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
