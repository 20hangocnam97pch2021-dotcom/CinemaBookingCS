using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

// Direct Actor: NV CSKH / Quản lý rạp
[Authorize(Roles = "CSKH,CinemaManager,Admin")]
public class ThongBaoController : Controller
{
    private readonly ApplicationDbContext _db;
    public ThongBaoController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    public async Task<IActionResult> DanhSach()
    {
        var ds = await _db.ThongBaos.OrderByDescending(t => t.ThoiGianGui).Take(50).ToListAsync();
        return View(ds);
    }

    // ================= BM17 - Mẫu thông báo khuyến mãi & nhắc lịch chiếu =================
    [HttpGet]
    public IActionResult SoanMoi() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SoanMoi(LoaiThongBao loaiThongBao, string[] kenhGui,
        string tieuDe, string noiDung, string[] doiTuongGui, DateTime? thoiGianGui)
    {
        if (kenhGui == null || kenhGui.Length == 0)
        {
            ViewBag.Loi = "Vui lòng chọn ít nhất 1 kênh gửi.";
            return View();
        }
        if (doiTuongGui == null || doiTuongGui.Length == 0)
        {
            ViewBag.Loi = "Vui lòng chọn ít nhất 1 đối tượng gửi.";
            return View();
        }

        // Mỗi kênh gửi tạo 1 bản ghi thông báo riêng (Email/SMS/Push gửi độc lập)
        foreach (var kenhStr in kenhGui)
        {
            if (!Enum.TryParse<KenhGui>(kenhStr, out var kenh)) continue;

            _db.ThongBaos.Add(new ThongBao
            {
                MaThongBao = "TB" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
                LoaiThongBao = loaiThongBao,
                KenhGui = kenh,
                TieuDe = tieuDe,
                NoiDung = noiDung,
                // Lưu đối tượng gửi (checkbox nhiều) - lấy giá trị đầu tiên làm chính, ghi rõ trong nội dung
                DoiTuongGui = doiTuongGui.Contains("TatCaKh") ? DoiTuongGuiThongBao.TatCaKh
                            : Enum.TryParse<DoiTuongGuiThongBao>(doiTuongGui[0], out var dt) ? dt
                            : DoiTuongGuiThongBao.TatCaKh,
                ThoiGianGui = thoiGianGui ?? DateTime.Now,
                MaNvTao = MaNguoiDungHienTai,
                TrangThai = TrangThaiThongBao.DangCho // TODO: thực tế cần job gửi mail/SMS thật (Hangfire), hiện chỉ lưu bản ghi
            });
        }

        await _db.SaveChangesAsync();
        TempData["ThongBao"] = "Đã lưu và lên lịch gửi thông báo.";
        return RedirectToAction(nameof(DanhSach));
    }
}
