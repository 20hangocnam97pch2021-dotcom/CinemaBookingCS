using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "Usher,Admin,CinemaManager")]
public class SoatVeController : Controller
{
    private readonly ApplicationDbContext _db;
    public SoatVeController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    // ================= BM05 - Phiếu xác nhận vé vào rạp (Soát vé, Direct Actor: NV soát vé/Usher) =================
    [HttpGet]
    public IActionResult QuetMaQr() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> XacNhanVe(string maQr)
    {
        var ve = await _db.Ves
            .Include(v => v.DatVe).ThenInclude(d => d!.LichChieu)
            .FirstOrDefaultAsync(v => v.MaQr == maQr);

        if (ve == null)
        {
            ViewBag.Loi = "Không tìm thấy vé với mã QR này.";
            return View("QuetMaQr");
        }

        if (ve.TrangThai == TrangThaiVe.DaSuDung)
        {
            ViewBag.Loi = "Vé này đã được sử dụng trước đó — không hợp lệ (chống soát vé 2 lần).";
            return View("QuetMaQr", ve);
        }
        if (ve.TrangThai == TrangThaiVe.KhongHopLe)
        {
            ViewBag.Loi = "Vé không hợp lệ.";
            return View("QuetMaQr", ve);
        }

        // QD07: cổng soát mở từ -30' đến +15' giờ chiếu (QR_SCAN_WINDOW_MINUTES)
        var lichChieu = ve.DatVe!.LichChieu!;
        var gioChieu = lichChieu.NgayChieu.ToDateTime(lichChieu.GioBatDau);
        var now = DateTime.Now;
        if (now < gioChieu.AddMinutes(-30) || now > gioChieu.AddMinutes(15))
        {
            ViewBag.Loi = $"Ngoài khung giờ soát vé cho phép (mở từ {gioChieu.AddMinutes(-30):HH:mm} đến {gioChieu.AddMinutes(15):HH:mm}).";
            return View("QuetMaQr", ve);
        }

        ve.TrangThai = TrangThaiVe.DaSuDung;
        ve.ThoiGianSoat = now;
        ve.MaNvSoat = MaNguoiDungHienTai;
        await _db.SaveChangesAsync();

        ViewBag.ThanhCong = true;
        return View("QuetMaQr", ve);
    }
}
