using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "CinemaManager,Admin")]
public class KhuyenMaiController : Controller
{
    private readonly ApplicationDbContext _db;
    public KhuyenMaiController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    public async Task<IActionResult> DanhSach()
    {
        var ds = await _db.KhuyenMais.OrderByDescending(k => k.NgayBatDau).ToListAsync();
        return View(ds);
    }

    // ================= BM21 - Phiếu quản lý chương trình khuyến mãi =================
    [HttpGet]
    public IActionResult TaoMoi() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TaoMoi(string tenKm, LoaiKhuyenMai loaiKm, decimal giaTriGiam,
        string dieuKienApDung, DateOnly ngayBatDau, DateOnly ngayKetThuc, int soLuongMaToiDa,
        string[] doiTuongApDung, TrangThaiKhuyenMai trangThai)
    {
        // QD24: giảm % tối đa 50%
        if (loaiKm == LoaiKhuyenMai.GiamPhanTram && giaTriGiam > 50)
        {
            ViewBag.Loi = "Khuyến mãi theo % tối đa chỉ được giảm 50% (QD24).";
            return View();
        }
        if (ngayBatDau >= ngayKetThuc)
        {
            ViewBag.Loi = "Ngày bắt đầu phải trước ngày kết thúc.";
            return View();
        }

        _db.KhuyenMais.Add(new KhuyenMai
        {
            MaKm = "KM" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            TenKm = tenKm,
            LoaiKm = loaiKm,
            GiaTriGiam = giaTriGiam,
            DieuKienApDung = dieuKienApDung,
            NgayBatDau = ngayBatDau,
            NgayKetThuc = ngayKetThuc,
            SoLuongMaToiDa = soLuongMaToiDa,
            DoiTuongApDung = string.Join(",", doiTuongApDung), // giữ checkbox - chọn được nhiều đối tượng
            MaNvTao = MaNguoiDungHienTai,
            TrangThai = trangThai
        });
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = $"Đã tạo chương trình khuyến mãi \"{tenKm}\".";
        return RedirectToAction(nameof(DanhSach));
    }
}
