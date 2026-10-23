using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize]
public class DanhGiaController : Controller
{
    private readonly ApplicationDbContext _db;
    public DanhGiaController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
    private string? LoaiTaiKhoanHienTai => User.FindFirst("LoaiTaiKhoan")?.Value;

    // ================= BM20 - Phiếu đánh giá phim (Direct Actor: Khách hàng) =================
    [HttpGet]
    public async Task<IActionResult> DanhGia(string maDatVe)
    {
        if (LoaiTaiKhoanHienTai != "Khách hàng")
        {
            TempData["ThongBao"] = "Chỉ tài khoản Khách hàng mới đánh giá phim được.";
            return RedirectToAction("LichChieu", "Phim");
        }
        var datVe = await _db.DatVes.Include(d => d.LichChieu).Include(d => d.DanhSachVe)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe && d.MaKh == MaNguoiDungHienTai);
        if (datVe == null) return NotFound();

        // QD23: CHỈ đánh giá khi vé đã ở trạng thái USED thật sự (đã quét QR qua BM05),
        // không chỉ dựa vào ngày chiếu đã qua hay chưa — khách có thể đặt vé nhưng không đến rạp.
        if (!datVe.DanhSachVe.Any(v => v.TrangThai == TrangThaiVe.DaSuDung))
        {
            TempData["ThongBao"] = "Chỉ có thể đánh giá phim sau khi vé đã được soát (quét QR) tại rạp.";
            return RedirectToAction("LichSu", "DatVe");
        }

        // Trong vòng 7 ngày sau ngày chiếu (REVIEW_DEADLINE_DAYS)
        var ngayChieu = datVe.NgayChieuSnapshot.ToDateTime(TimeOnly.MinValue);
        if (DateTime.Now > ngayChieu.AddDays(7))
        {
            TempData["ThongBao"] = "Đã quá thời hạn đánh giá (7 ngày sau ngày chiếu).";
            return RedirectToAction("LichSu", "DatVe");
        }

        ViewBag.DatVe = datVe;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DanhGia(string maDatVe, string maPhim, int soSao, string? nhanXet)
    {
        if (LoaiTaiKhoanHienTai != "Khách hàng")
        {
            TempData["ThongBao"] = "Chỉ tài khoản Khách hàng mới đánh giá phim được.";
            return RedirectToAction("LichChieu", "Phim");
        }
        // QD23: unique (ma_kh, ma_phim) - 1 đánh giá/phim/KH
        if (await _db.DanhGias.AnyAsync(x => x.MaKh == MaNguoiDungHienTai && x.MaPhim == maPhim))
        {
            TempData["ThongBao"] = "Bạn đã đánh giá phim này rồi.";
            return RedirectToAction("LichSu", "DatVe");
        }

        _db.DanhGias.Add(new DanhGia
        {
            MaDanhGia = "DG" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            MaDatVe = maDatVe,
            MaKh = MaNguoiDungHienTai,
            MaPhim = maPhim,
            SoSao = soSao,
            NhanXet = nhanXet,
            NgayDanhGia = DateOnly.FromDateTime(DateTime.Now),
            TrangThai = TrangThaiDanhGia.HienThi
        });

        // Cập nhật phi chuẩn hoá (mục 5.1): điểm trung bình + số lượt đánh giá trên PHIM
        var phim = await _db.Phims.FindAsync(maPhim);
        if (phim != null)
        {
            var tongSaoCu = phim.DiemDanhGiaTb * phim.SoLuotDanhGia;
            phim.SoLuotDanhGia += 1;
            phim.DiemDanhGiaTb = (tongSaoCu + soSao) / phim.SoLuotDanhGia;
        }

        await _db.SaveChangesAsync();
        TempData["ThongBao"] = "Cảm ơn bạn đã gửi đánh giá!";
        return RedirectToAction("LichSu", "DatVe");
    }
}
