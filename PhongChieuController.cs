using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "CinemaManager,Admin")]
public class PhongChieuController : Controller
{
    private readonly ApplicationDbContext _db;
    public PhongChieuController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> DanhSach()
    {
        var ds = await _db.PhongChieus.Where(p => !p.IsDeleted).ToListAsync();
        return View(ds);
    }

    // ================= BM09 - Phiếu quản lý phòng chiếu và ghế ngồi =================
    // ĐÃ SỬA theo bản UI: bỏ trường "Số ghế mỗi hàng" (mâu thuẫn với bảng chi tiết vì mỗi hàng
    // số ghế khác nhau), thay bằng radio "Loại phòng". Đã bỏ cột "Giá bổ sung (VNĐ)" khỏi cấu
    // hình hàng ghế - giá vé chỉ quản lý duy nhất ở BM08 (LichChieuController).
    [HttpGet]
    public IActionResult TaoMoi() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TaoMoi(PhongChieu phong, string[] hangGhe, int[] soGheTrongHang, string[] loaiGhe)
    {
        phong.MaPhong = "P" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        _db.PhongChieus.Add(phong);

        int tongSoGhe = 0;
        for (int i = 0; i < hangGhe.Length; i++)
        {
            var loai = loaiGhe[i] == "VIP" ? LoaiGhe.VIP : LoaiGhe.Thuong;
            for (int soGhe = 1; soGhe <= soGheTrongHang[i]; soGhe++)
            {
                _db.Ghes.Add(new Ghe
                {
                    MaGhe = $"{phong.MaPhong}_{hangGhe[i]}{soGhe}",
                    MaPhong = phong.MaPhong,
                    HangGhe = hangGhe[i],
                    SoGhe = soGhe,
                    LoaiGhe = loai,
                    GiaBoSung = 0
                });
                tongSoGhe++;
            }
        }
        phong.SucChua = tongSoGhe; // QD11: sức chứa = tổng số ghế thực tế trong phòng
        // Số ghế VIP tự tính từ dữ liệu cấu hình hàng ghế (không nhận từ form)
        phong.SoGheVip = 0;
        for (int i = 0; i < hangGhe.Length; i++)
            if (loaiGhe[i] == "VIP") phong.SoGheVip += soGheTrongHang[i];

        await _db.SaveChangesAsync();
        TempData["ThongBao"] = $"Đã tạo phòng chiếu \"{phong.TenPhong}\" với {tongSoGhe} ghế.";
        return RedirectToAction(nameof(DanhSach));
    }
}
